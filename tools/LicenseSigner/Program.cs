using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoltPreload.Application.Licensing;
using BoltPreload.Infrastructure.Licensing;

// ============================================================================
// 授权签发工具（供应商侧，不随交付包发出）
//
// 背景：LocalFileLicenseService 对 license.json 做 RSA-SHA256 硬验签，验不过则
// LicenseGateMiddleware 拦掉全部写操作 —— 现场表现是"能看不能用"：建档、标定、
// 告警确认全部 403。此前仓库里只有验签端，没有任何能签出合法 license.json 的东西
// （deploy/license.example.json 的 signature 是占位串 PHASE1-DEMO-SIGNATURE），
// 也就是说按当时的状态交付出去，系统会永久只读。本工具补上签发侧。
//
// 私钥只留在供应商侧，公钥随现场配置（License:PublicKeyPem）下发。
// ============================================================================

const string PrivateKeyFileName = "license-private.pem";
const string PublicKeyFileName = "license-public.pem";

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

try
{
    return (args.Length == 0 ? "" : args[0].ToLowerInvariant()) switch
    {
        "keygen" => KeyGen(args),
        "sign" => Sign(args),
        "verify" => Verify(args),
        _ => Usage()
    };
}
catch (SignerError ex)
{
    Console.Error.WriteLine($"错误：{ex.Message}");
    return 2;
}

int Usage()
{
    Console.WriteLine("""
        授权签发工具（供应商侧使用，私钥不得进入仓库或交付包）

        1) 生成密钥对（每个产品线/客户体系一次即可，之后长期复用）
           LicenseSigner keygen [--out-dir <目录，默认当前目录>] [--key-size <2048|3072|4096，默认 3072>]
             产出 license-private.pem  → 供应商保管，用于签发
                 license-public.pem    → 填到现场 appsettings 的 License:PublicKeyPem

        2) 为某台工控机签发授权
           LicenseSigner sign --private-key <私钥.pem>
                              --machine-code <目标机机器码>
                              --licensed-to  <客户名称>
                              --license-id   <授权编号>
                              --expires      <yyyy-MM-dd>
                              --max-devices  <数量>
                              --max-channels <数量>
                              [--modules realtime-monitoring,torque-fit,wave-diagnostics,reports]
                              [--out license.json]
             机器码取自目标机：GET /api/license/machine-code（匿名可访问），
             或启动日志里的"机器码 XXXXXXXX"。

        3) 签发后本地预检（不必等到现场才发现签错）
           LicenseSigner verify --public-key <公钥.pem> --license <license.json>
        """);
    return 1;
}

int KeyGen(string[] argv)
{
    var outDir = Arg(argv, "--out-dir") ?? ".";
    var keySize = int.Parse(Arg(argv, "--key-size") ?? "3072", CultureInfo.InvariantCulture);
    if (keySize is not (2048 or 3072 or 4096))
        throw new SignerError("--key-size 只接受 2048 / 3072 / 4096");

    Directory.CreateDirectory(outDir);
    var privatePath = Path.Combine(outDir, PrivateKeyFileName);
    var publicPath = Path.Combine(outDir, PublicKeyFileName);

    // 不覆盖已有私钥：覆盖会让此前签发的所有 license 立刻失效（现场集体变只读）。
    if (File.Exists(privatePath))
        throw new SignerError(
            $"{privatePath} 已存在。覆盖会让所有已签发的 license 失效，请先确认后手工移走。");

    using var rsa = RSA.Create(keySize);
    File.WriteAllText(privatePath, rsa.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem());

    // 私钥权限收紧到仅属主可读（Windows 上 Unix 权限位无效，靠 NTFS ACL/离线保管）。
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    Console.WriteLine($"已生成 RSA-{keySize} 密钥对：");
    Console.WriteLine($"  私钥（供应商保管，切勿提交仓库）：{privatePath}");
    Console.WriteLine($"  公钥（下发现场配置）：            {publicPath}");
    Console.WriteLine();
    Console.WriteLine("现场注入方式（任选其一）：");
    Console.WriteLine("  环境变量：License__PublicKeyPem=\"$(cat license-public.pem)\"");
    Console.WriteLine("  或写入 appsettings.Production.json 的 License:PublicKeyPem");
    return 0;
}

int Sign(string[] argv)
{
    var privateKeyPath = Required(argv, "--private-key");
    var doc = new LicenseDocument(
        LicenseId: Required(argv, "--license-id"),
        LicensedTo: Required(argv, "--licensed-to"),
        MachineCode: Required(argv, "--machine-code"),
        ExpiresAt: ParseDate(Required(argv, "--expires")),
        MaxDevices: ParseCount(Required(argv, "--max-devices"), "--max-devices"),
        MaxChannels: ParseCount(Required(argv, "--max-channels"), "--max-channels"),
        Modules: (Arg(argv, "--modules") ?? "realtime-monitoring,torque-fit,wave-diagnostics,reports")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Signature: "");

    if (doc.ExpiresAt < DateOnly.FromDateTime(DateTime.UtcNow))
        throw new SignerError($"--expires {doc.ExpiresAt:yyyy-MM-dd} 已是过去的日期，签出来现场直接判过期。");

    using var rsa = RSA.Create();
    rsa.ImportFromPem(ReadKey(privateKeyPath, "私钥"));

    // 与验签端同一份规范化拼接
    var payload = LocalFileLicenseService.BuildSignaturePayload(doc);
    var signature = Convert.ToBase64String(
        rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    var signed = doc with { Signature = signature };
    var outPath = Arg(argv, "--out") ?? "license.json";
    File.WriteAllText(outPath, JsonSerializer.Serialize(signed, jsonOptions));

    Console.WriteLine($"已签发：{outPath}");
    PrintDoc(signed);
    Console.WriteLine();
    Console.WriteLine($"部署：把该文件放到工控机安装目录（默认与 License:LicensePath 一致，即 license.json）。");
    Console.WriteLine($"注意：机器码必须与目标机一致（当前签给 {signed.MachineCode}），换主板/网卡后需重新签发。");
    return 0;
}

int Verify(string[] argv)
{
    var publicKeyPath = Required(argv, "--public-key");
    var licensePath = Required(argv, "--license");

    var doc = JsonSerializer.Deserialize<LicenseDocument>(File.ReadAllText(licensePath), jsonOptions)
        ?? throw new SignerError($"{licensePath} 解析失败");

    using var rsa = RSA.Create();
    rsa.ImportFromPem(ReadKey(publicKeyPath, "公钥"));

    var payload = LocalFileLicenseService.BuildSignaturePayload(doc);
    bool sigOk;
    try
    {
        sigOk = rsa.VerifyData(Encoding.UTF8.GetBytes(payload),
            Convert.FromBase64String(doc.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
    catch (FormatException)
    {
        throw new SignerError("signature 不是合法 Base64");
    }

    var expired = DateOnly.FromDateTime(DateTime.UtcNow) > doc.ExpiresAt;
    PrintDoc(doc);
    Console.WriteLine($"  签名校验：{(sigOk ? "通过" : "失败")}");
    Console.WriteLine($"  有效期：  {(expired ? "已过期" : "未过期")}");
    Console.WriteLine("  机器码：  只能在目标机上比对（本机机器码与客户机不同，此处不做判断）");

    if (!sigOk || expired)
    {
        Console.Error.WriteLine("预检未通过，请勿下发。");
        return 3;
    }
    Console.WriteLine("预检通过。");
    return 0;
}

void PrintDoc(LicenseDocument d)
{
    Console.WriteLine($"  授权编号：{d.LicenseId}");
    Console.WriteLine($"  授权给：  {d.LicensedTo}");
    Console.WriteLine($"  机器码：  {d.MachineCode}");
    Console.WriteLine($"  到期：    {d.ExpiresAt:yyyy-MM-dd}");
    Console.WriteLine($"  容量：    设备 {d.MaxDevices} / 通道 {d.MaxChannels}");
    Console.WriteLine($"  模块：    {string.Join(", ", d.Modules)}");
}

static string ReadKey(string path, string what)
{
    if (!File.Exists(path)) throw new SignerError($"{what}文件不存在：{path}");
    return File.ReadAllText(path);
}

static DateOnly ParseDate(string s) =>
    DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        ? d
        : throw new SignerError($"--expires 需要 yyyy-MM-dd 格式，收到：{s}");

static int ParseCount(string s, string name) =>
    int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
        ? n
        : throw new SignerError($"{name} 需要正整数，收到：{s}");

static string? Arg(string[] argv, string name)
{
    var i = Array.FindIndex(argv, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

static string Required(string[] argv, string name) =>
    Arg(argv, name) ?? throw new SignerError($"缺少必填参数 {name}");

/// <summary>参数/密钥/校验类错误：只打印可读信息，不打印堆栈（这是给运维和商务用的命令行工具）。</summary>
internal sealed class SignerError(string message) : Exception(message);
