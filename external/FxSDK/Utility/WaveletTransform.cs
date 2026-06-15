namespace FxSDK.Utility;

/// <summary>
/// 小波变换
/// </summary>
public class WaveletTransform
{
    public static unsafe void FastForward1d(double[] input, out double[] output, Wavelet wavelet, int level)
    {
        var len = wavelet.DecompositionLow.Length;
        int circleInd;
        output = new double[input.Length];
        var buff = new double[input.Length];
        Buffer.BlockCopy(input, 0, output, 0, input.Length * 8);
        double bufScal = 0;
        double bufDet = 0;
        var decLow = stackalloc double[len];
        var decHigh = stackalloc double[len];
        for (var i = 0; i < len; i++)
        {
            decLow[i] = wavelet.DecompositionLow[i];
            decHigh[i] = wavelet.DecompositionHigh[i];
        }

        fixed (double* pout = output, pbuf = buff)
        {
            for (var mLevel = 0; mLevel < level; mLevel++)
            {
                var bound = input.Length >> mLevel;
                var startIndex = -((len >> 1) - 1);
                Buffer.BlockCopy(output, 0, buff, 0, bound * 8);

                for (var i = 0; i < bound >> 1; i++)
                {
                    for (int j = startIndex, k = 0; k < len; j++, k++)
                    {
                        if ((startIndex < 0) || j >= bound) circleInd = ((j % bound) + bound) % bound;
                        else circleInd = j;
                        bufScal += decLow[k] * pout[circleInd];
                        bufDet += decHigh[k] * pout[circleInd];
                    }

                    startIndex += 2;
                    pbuf[i] = bufScal;
                    pbuf[i + (bound >> 1)] = bufDet;
                    bufScal = 0;
                    bufDet = 0;
                }

                Buffer.BlockCopy(buff, 0, output, 0, bound * 8);
            }
        }
    }
    public static unsafe void FastInverse1d(double[] input, out double[] output, Wavelet wavelet, int startLevel)
    {
        var len = wavelet.ReconstructionLow.Length;
        int circleInd;
        output = new double[input.Length];
        var buff = new double[input.Length];
        var bufferLow = new double[input.Length];
        var bufferHigh = new double[input.Length];
        Buffer.BlockCopy(input, 0, output, 0, input.Length * 8);
        double buf = 0;
        var recLow = stackalloc double[len];
        var recHigh = stackalloc double[len];
        for (var i = 0; i < len; i++)
        {
            recLow[i] = wavelet.ReconstructionLow[i];
            recHigh[i] = wavelet.ReconstructionHigh[i];
        }

        fixed (double* pbuf = buff, pLow = bufferLow, pHigh = bufferHigh)
        {
            for (var level = startLevel; level > 0; level--)
            {
                var bound = input.Length >> level;
                var startIndex = -((len >> 1) - 1);
                for (int i = 0, j = 0; i < bound << 1; i += 2, j++)
                {
                    pLow[i] = 0;
                    pHigh[i] = 0;
                    pLow[i + 1] = output[j];
                    pHigh[i + 1] = output[bound + j];
                }

                for (var i = 0; i < bound << 1; i++)
                {
                    for (int j = startIndex, k = 0; k < len; j++, k++)
                    {
                        if ((startIndex < 0) || j >= (bound << 1))
                            circleInd = (j % (bound << 1) + (bound << 1)) % (bound << 1);
                        else circleInd = j;
                        buf += recLow[k] * pLow[circleInd] + recHigh[k] * pHigh[circleInd];
                    }

                    startIndex += 1;
                    pbuf[i] = buf;
                    buf = 0;
                }

                Buffer.BlockCopy(buff, 0, output, 0, bound * 16);
            }
        }
    }
    private static double[] StepForward(double[] input, Wavelet wavelet, int currentLevel)
    {
        var bound = input.Length >> currentLevel;
        var len = wavelet.DecompositionLow.Length;
        var startIndex = -((len >> 1) - 1);
        var output = new double[input.Length];
        Array.Copy(input, bound, output, bound, input.Length - bound);
        double bufScal = 0;
        double bufDet = 0;
        int circleInd;
        for (int i = 0, r = 0; i < bound; i += 2, r++)
        {
            for (int j = startIndex, k = 0; k < len; j++, k++)
            {
                if ((startIndex < 0) || j >= bound) circleInd = ((j % bound) + bound) % bound;
                else circleInd = j;
                bufScal += wavelet.DecompositionLow[k] * input[circleInd];
                bufDet += wavelet.DecompositionHigh[k] * input[circleInd];
            }

            startIndex += 2;
            output[r] = bufScal;
            output[r + (bound >> 1)] = bufDet;
            bufScal = 0;
            bufDet = 0;
        }

        return output;
    }
    public static void Forward1D(double[] input, out double[] output, Wavelet wavelet, int level)
    {
        var f = (input.Length & (input.Length - 1)) == 0;
        if (!f || input.Length == 0) throw new Exception("input length must be a power of two");

        output = new double[input.Length];
        Array.Copy(input, output, input.Length);
        for (var i = 0; i < level; i++)
        {
            output = StepForward(output, wavelet, i);
        }
    }
    private static double[] StepInverse(double[] input, Wavelet wavelet, int currentLevel)
    {
        var bound = input.Length >> currentLevel;
        var len = wavelet.ReconstructionLow.Length;
        var startIndex = -((len >> 1) - 1);
        var output = new double[input.Length];
        var buffLow = new double[bound << 1];
        var buffHi = new double[bound << 1];
        Array.Copy(input, 0, output, 0, input.Length);
        double bufScal = 0;
        int circleInd;
        for (int i = 0, j = 0; i < bound << 1; i += 2, j++)
        {
            buffLow[i] = 0;
            buffHi[i] = 0;
            buffLow[i + 1] = input[j];
            buffHi[i + 1] = input[bound + j];
        }

        for (var i = 0; i < bound << 1; i++)
        {
            for (int j = startIndex, k = 0; k < len; j++, k++)
            {
                if ((startIndex < 0) || j >= (bound << 1)) circleInd = (j % (bound << 1) + (bound << 1)) % (bound << 1);
                else circleInd = j;
                bufScal += wavelet.ReconstructionLow[k] * buffLow[circleInd] +
                           wavelet.ReconstructionHigh[k] * buffHi[circleInd];
            }

            startIndex += 1;
            output[i] = bufScal;
            bufScal = 0;
        }

        return output;
    }
    public static void Inverse1D(double[] input, out double[] output, Wavelet wavelet, int startLevel)
    {
        var f = (input.Length & (input.Length - 1)) == 0;
        if (!f || input.Length == 0) throw new Exception("input length must be a power of two");

        output = new double[input.Length];
        Array.Copy(input, output, input.Length);
        for (var i = startLevel; i > 0; i--)
        {
            output = StepInverse(output, wavelet, i);
        }
    }
    private static unsafe void FastStepForward(ref double[] input, double[] buff, int len, int currentLevel,
        double* decLow, double* decHigh)
    {
        int circleInd;
        double bufScal = 0;
        double bufDet = 0;
        var bound = input.Length >> currentLevel;
        var startIndex = -((len >> 1) - 1);
        fixed (double* pinp = input, pbuf = buff)
        {
            for (var i = 0; i < bound >> 1; i++)
            {
                for (int j = startIndex, k = 0; k < len; j++, k++)
                {
                    if ((startIndex < 0) || j >= bound) circleInd = ((j % bound) + bound) % bound;
                    else circleInd = j;
                    bufScal += decLow[k] * pinp[circleInd];
                    bufDet += decHigh[k] * pinp[circleInd];
                }

                startIndex += 2;
                pbuf[i] = bufScal;
                pbuf[i + (bound >> 1)] = bufDet;
                bufScal = 0;
                bufDet = 0;
            }

            Buffer.BlockCopy(buff, 0, input, 0, bound * 8);
        }
    }
    private static unsafe void FastStepInverse(ref double[] input, double[] buffLow, double[] buffHigh, int len,
        int currentLevel, double* recLow, double* recHigh)
    {
        var bound = input.Length >> currentLevel;
        var startIndex = -((len >> 1) - 1);
        double buf = 0;
        int circleInd;
        fixed (double* pinp = input, pbufLow = buffLow, pbufHi = buffHigh)
        {
            for (int i = 0, j = 0; i < bound << 1; i += 2, j++)
            {
                pbufLow[i] = 0;
                pbufHi[i] = 0;
                pbufLow[i + 1] = pinp[j];
                pbufHi[i + 1] = pinp[bound + j];
            }

            for (var i = 0; i < bound << 1; i++)
            {
                for (int j = startIndex, k = 0; k < len; j++, k++)
                {
                    if ((startIndex < 0) || j >= (bound << 1))
                        circleInd = (j % (bound << 1) + (bound << 1)) % (bound << 1);
                    else circleInd = j;
                    buf += recLow[k] * pbufLow[circleInd] + recHigh[k] * pbufHi[circleInd];
                }

                startIndex += 1;
                pinp[i] = buf;
                buf = 0;
            }
        }
    }
    
    public static unsafe void FastForward2d(double[,] input, out double[,] output, Wavelet wavelet, int level)
    {
        var dataLen = input.GetLength(0);
        var len = wavelet.DecompositionHigh.Length;
        int bound;
        output = new double[dataLen, dataLen];
        var buff = new double[dataLen];
        var buffData = new double[dataLen];

        var decLow = stackalloc double[len];
        var decHigh = stackalloc double[len];
        for (var i = 0; i < len; i++)
        {
            decLow[i] = wavelet.DecompositionLow[i];
            decHigh[i] = wavelet.DecompositionHigh[i];
        }

        for (var i = 0; i < dataLen; i++)
        {
            for (var j = 0; j < dataLen; j++)
            {
                output[i, j] = input[i, j];
            }
        }

        for (var lev = 0; lev < level; lev++)
        {
            bound = dataLen >> lev;
            for (var i = 0; i < bound; i++)
            {
                for (var j = 0; j < bound; j++) buffData[j] = output[i, j];
                FastStepForward(ref buffData, buff, len, lev, decLow, decHigh);
                for (var j = 0; j < bound; j++) output[i, j] = buffData[j];
            }

            for (var j = 0; j < bound; j++)
            {
                for (var i = 0; i < bound; i++) buffData[i] = output[i, j];
                FastStepForward(ref buffData, buff, len, lev, decLow, decHigh);
                for (var i = 0; i < bound; i++) output[i, j] = buffData[i];
            }
        }
    }

    public static unsafe void FastInverse2d(double[,] input, out double[,] output, Wavelet wavelet, int level)
    {
        var dataLen = input.GetLength(0);
        var len = wavelet.DecompositionHigh.Length;
        int bound;
        output = new double[dataLen, dataLen];
        var buffData = new double[dataLen];
        var buffLow = new double[dataLen];
        var buffHigh = new double[dataLen];

        var recLow = stackalloc double[len];
        var recHigh = stackalloc double[len];
        for (var i = 0; i < len; i++)
        {
            recLow[i] = wavelet.ReconstructionLow[i];
            recHigh[i] = wavelet.ReconstructionHigh[i];
        }

        for (var i = 0; i < dataLen; i++)
        {
            for (var j = 0; j < dataLen; j++)
            {
                output[i, j] = input[i, j];
            }
        }

        for (var lev = level; lev > 0; lev--)
        {
            bound = dataLen >> lev;
            for (var j = 0; j < bound << 1; j++)
            {
                for (var i = 0; i < bound << 1; i++) buffData[i] = output[i, j];
                FastStepInverse(ref buffData, buffLow, buffHigh, len, lev, recLow, recHigh);
                for (var i = 0; i < bound << 1; i++) output[i, j] = buffData[i];
            }

            for (var i = 0; i < bound << 1; i++)
            {
                for (var j = 0; j < bound << 1; j++) buffData[j] = output[i, j];
                FastStepInverse(ref buffData, buffLow, buffHigh, len, lev, recLow, recHigh);
                for (var j = 0; j < bound << 1; j++) output[i, j] = buffData[j];
            }
        }
    }

    public static double[] GetAllDetail(double[] input, int currentLevel)
    {
        var shift = input.Length >> currentLevel;
        var output = new double[input.Length - shift];
        Array.Copy(input, shift, output, 0, input.Length - shift);
        return output;
    }

    public static double[] GetAllDetail(double[,] input, int currentLevel)
    {
        var len = input.GetLength(0) >> currentLevel;
        var output = new double[input.GetLength(0) * input.GetLength(0) - (len * len)];
        var outIndex = 0;

        for (var lev = currentLevel; lev > 0; lev--)
        {
            var bound = input.GetLength(0) >> lev - 1;
            len = input.GetLength(0) >> lev;
            for (var i = 0; i < len; i++)
            {
                for (var j = len; j < bound; j++)
                {
                    output[outIndex] = input[i, j];
                    outIndex++;
                }
            }

            for (var i = len; i < bound; i++)
            {
                for (var j = 0; j < len; j++)
                {
                    output[outIndex] = input[i, j];
                    outIndex++;
                }
            }

            for (var i = len; i < bound; i++)
            {
                for (var j = len; j < bound; j++)
                {
                    output[outIndex] = input[i, j];
                    outIndex++;
                }
            }
        }

        return output;
    }

    public static double[] GetDetailOfLevel(double[,] input, int level)
    {
        var len = input.GetLength(0) >> level;
        var lev = level;
        var bound = input.GetLength(0) >> lev - 1;

        var output = new double[bound * bound - (len * len)];
        var outIndex = 0;

        for (var i = 0; i < len; i++)
        {
            for (var j = len; j < bound; j++)
            {
                output[outIndex] = input[i, j];
                outIndex++;
            }
        }

        for (var i = len; i < bound; i++)
        {
            for (var j = 0; j < len; j++)
            {
                output[outIndex] = input[i, j];
                outIndex++;
            }
        }

        for (var i = len; i < bound; i++)
        {
            for (var j = len; j < bound; j++)
            {
                output[outIndex] = input[i, j];
                outIndex++;
            }
        }

        return output;
    }

    public static void SetAllDetail(double[] input, double[,] coefs, int currentLevel)
    {
        var len = coefs.GetLength(0) >> currentLevel;
        var inpIndex = 0;
        
        for (var lev = currentLevel; lev > 0; lev--)
        {
            var bound = coefs.GetLength(0) >> lev - 1;
            len = coefs.GetLength(0) >> lev;
            for (var i = 0; i < len; i++)
            {
                for (var j = len; j < bound; j++)
                {
                    coefs[i, j] = input[inpIndex];
                    inpIndex++;
                }
            }

            for (var i = len; i < bound; i++)
            {
                for (var j = 0; j < len; j++)
                {
                    coefs[i, j] = input[inpIndex];
                    inpIndex++;
                }
            }

            for (var i = len; i < bound; i++)
            {
                for (var j = len; j < bound; j++)
                {
                    coefs[i, j] = input[inpIndex];
                    inpIndex++;
                }
            }
        }
    }

    public static void SetDetailOfLevel(double[] input, double[,] coefs, int level)
    {
        var len = coefs.GetLength(0) >> level;
        var inpIndex = 0;

        var lev = level;
        var bound = coefs.GetLength(0) >> lev - 1;
        len = coefs.GetLength(0) >> lev;
        for (var i = 0; i < len; i++)
        {
            for (var j = len; j < bound; j++)
            {
                coefs[i, j] = input[inpIndex];
                inpIndex++;
            }
        }

        for (var i = len; i < bound; i++)
        {
            for (var j = 0; j < len; j++)
            {
                coefs[i, j] = input[inpIndex];
                inpIndex++;
            }
        }

        for (var i = len; i < bound; i++)
        {
            for (var j = len; j < bound; j++)
            {
                coefs[i, j] = input[inpIndex];
                inpIndex++;
            }
        }
    }

    public static double[] GetDetailOfLevel(double[] input, int currentLevel, int level)
    {
        var len = input.Length >> level;
        var output = new double[len];
        Array.Copy(input, len, output, 0, len);
        return output;
    }

    public static double[] GetScaling(double[] input, int currentLevel)
    {
        var len = input.Length >> currentLevel;
        var output = new double[len];
        Array.Copy(input, output, len);
        return output;
    }

    public static void SetDetailOfLevel(double[] input, double[] inpDetails)
    {
        Array.Copy(inpDetails, 0, input, inpDetails.Length, inpDetails.Length);
    }
    
    public static double[] SoftThreshold(double[] coefs, double lambda)
    {
        var result = new double[coefs.Length];
        for (var i = 0; i < coefs.Length; i++)
        {
            if (Math.Abs(coefs[i]) > lambda)
                result[i] = Math.Sign(coefs[i]) * (Math.Abs(coefs[i]) - lambda);
            else
                result[i] = 0;
        }
        return result;
    }
}

public class Wavelet
{
    internal readonly double[] DecompositionLow;
    internal readonly double[] DecompositionHigh;

    internal readonly double[] ReconstructionLow;
    internal readonly double[] ReconstructionHigh;

    public string Name;

    public Int32 FilterLength => DecompositionLow.Length;

    private Wavelet(string name, double[] decLowPass) //For orthogonal wavelets
    {
        Name = name;
        DecompositionLow = decLowPass;
        DecompositionHigh = new double[decLowPass.Length];
        ReconstructionLow = new double[decLowPass.Length];
        ReconstructionHigh = new double[decLowPass.Length];

        for (var i = 0; i < decLowPass.Length; i++)
        {
            DecompositionHigh[i] = decLowPass[decLowPass.Length - i - 1];
            if (i % 2 != 0) DecompositionHigh[i] *= -1;
            ReconstructionLow[i] = decLowPass[decLowPass.Length - i - 1];
        }

        for (var i = 0; i < decLowPass.Length; i++)
            ReconstructionHigh[i] = DecompositionHigh[decLowPass.Length - i - 1];
    }

    #region Daubechies 小波系数
    public static Wavelet Db1 => new(nameof(Db1), new[]
    {
        0.7071067812, 0.7071067812
    });
    public static Wavelet Db2 => new(nameof(Db2), new[]
    {
        0.48296291314469025, 0.836516303737469, 0.22414386804185735, -0.12940952255092145
    });
    public static Wavelet Db3 => new(nameof(Db3), new[]
    {
        0.3326705529509569, 0.8068915093133388, 0.4598775021193313, -0.13501102001039084, -0.08544127388224149, 0.035226291882100656
    });
    public static Wavelet Db4 => new(nameof(Db4), new[] 
    {
        2.303778133088965008632911830440708500016152482483092977910968e-01,
        7.148465705529156470899219552739926037076084010993081758450110e-01,
        6.308807679298589078817163383006152202032229226771951174057473e-01,
        -2.798376941685985421141374718007538541198732022449175284003358e-02,
        -1.870348117190930840795706727890814195845441743745800912057770e-01,
        3.084138183556076362721936253495905017031482172003403341821219e-02,
        3.288301166688519973540751354924438866454194113754971259727278e-02,
        -1.059740178506903210488320852402722918109996490637641983484974e-02
    });
    public static Wavelet Db5 => new(nameof(Db5), new[] 
    {
        1.601023979741929144807237480204207336505441246250578327725699e-01,
        6.038292697971896705401193065250621075074221631016986987969283e-01,
        7.243085284377729277280712441022186407687562182320073725767335e-01,
        1.384281459013207315053971463390246973141057911739561022694652e-01,
        -2.422948870663820318625713794746163619914908080626185983913726e-01,
        -3.224486958463837464847975506213492831356498416379847225434268e-02,
        7.757149384004571352313048938860181980623099452012527983210146e-02,
        -6.241490212798274274190519112920192970763557165687607323417435e-03,
        -1.258075199908199946850973993177579294920459162609785020169232e-02,
        3.335725285473771277998183415817355747636524742305315099706428e-03
    });
    public static Wavelet Db6 => new(nameof(Db6), new[]
    {
        1.115407433501094636213239172409234390425395919844216759082360e-01,
        4.946238903984530856772041768778555886377863828962743623531834e-01,
        7.511339080210953506789344984397316855802547833382612009730420e-01,
        3.152503517091976290859896548109263966495199235172945244404163e-01,
        -2.262646939654398200763145006609034656705401539728969940143487e-01,
        -1.297668675672619355622896058765854608452337492235814701599310e-01,
        9.750160558732304910234355253812534233983074749525514279893193e-02,
        2.752286553030572862554083950419321365738758783043454321494202e-02,
        -3.158203931748602956507908069984866905747953237314842337511464e-02,
        5.538422011614961392519183980465012206110262773864964295476524e-04,
        4.777257510945510639635975246820707050230501216581434297593254e-03,
        -1.077301085308479564852621609587200035235233609334419689818580e-03
    });
    public static Wavelet Db7 => new(nameof(Db7), new[]
    {
        7.785205408500917901996352195789374837918305292795568438702937e-02,
        3.965393194819173065390003909368428563587151149333287401110499e-01,
        7.291320908462351199169430703392820517179660611901363782697715e-01,
        4.697822874051931224715911609744517386817913056787359532392529e-01,
        -1.439060039285649754050683622130460017952735705499084834401753e-01,
        -2.240361849938749826381404202332509644757830896773246552665095e-01,
        7.130921926683026475087657050112904822711327451412314659575113e-02,
        8.061260915108307191292248035938190585823820965629489058139218e-02,
        -3.802993693501441357959206160185803585446196938467869898283122e-02,
        -1.657454163066688065410767489170265479204504394820713705239272e-02,
        1.255099855609984061298988603418777957289474046048710038411818e-02,
        4.295779729213665211321291228197322228235350396942409742946366e-04,
        -1.801640704047490915268262912739550962585651469641090625323864e-03,
        3.537137999745202484462958363064254310959060059520040012524275e-04
    });
    public static Wavelet Db8 => new(nameof(Db8), new[]
    {
        5.441584224310400995500940520299935503599554294733050397729280e-02,
        3.128715909142999706591623755057177219497319740370229185698712e-01,
        6.756307362972898068078007670471831499869115906336364227766759e-01,
        5.853546836542067127712655200450981944303266678053369055707175e-01,
        -1.582910525634930566738054787646630415774471154502826559735335e-02,
        -2.840155429615469265162031323741647324684350124871451793599204e-01,
        4.724845739132827703605900098258949861948011288770074644084096e-04,
        1.287474266204784588570292875097083843022601575556488795577000e-01,
        -1.736930100180754616961614886809598311413086529488394316977315e-02,
        -4.408825393079475150676372323896350189751839190110996472750391e-02,
        1.398102791739828164872293057263345144239559532934347169146368e-02,
        8.746094047405776716382743246475640180402147081140676742686747e-03,
        -4.870352993451574310422181557109824016634978512157003764736208e-03,
        -3.917403733769470462980803573237762675229350073890493724492694e-04,
        6.754494064505693663695475738792991218489630013558432103617077e-04,
        -1.174767841247695337306282316988909444086693950311503927620013e-04
    });
    public static Wavelet Db9 => new(nameof(Db9), new[]
    {
        3.807794736387834658869765887955118448771714496278417476647192e-02,
        2.438346746125903537320415816492844155263611085609231361429088e-01,
        6.048231236901111119030768674342361708959562711896117565333713e-01,
        6.572880780513005380782126390451732140305858669245918854436034e-01,
        1.331973858250075761909549458997955536921780768433661136154346e-01,
        -2.932737832791749088064031952421987310438961628589906825725112e-01,
        -9.684078322297646051350813353769660224825458104599099679471267e-02,
        1.485407493381063801350727175060423024791258577280603060771649e-01,
        3.072568147933337921231740072037882714105805024670744781503060e-02,
        -6.763282906132997367564227482971901592578790871353739900748331e-02,
        2.509471148314519575871897499885543315176271993709633321834164e-04,
        2.236166212367909720537378270269095241855646688308853754721816e-02,
        -4.723204757751397277925707848242465405729514912627938018758526e-03,
        -4.281503682463429834496795002314531876481181811463288374860455e-03,
        1.847646883056226476619129491125677051121081359600318160732515e-03,
        2.303857635231959672052163928245421692940662052463711972260006e-04,
        -2.519631889427101369749886842878606607282181543478028214134265e-04,
        3.934732031627159948068988306589150707782477055517013507359938e-05
    });
    public static Wavelet Db10 => new(nameof(Db10), new[]
    {
        2.667005790055555358661744877130858277192498290851289932779975e-02,
        1.881768000776914890208929736790939942702546758640393484348595e-01,
        5.272011889317255864817448279595081924981402680840223445318549e-01,
        6.884590394536035657418717825492358539771364042407339537279681e-01,
        2.811723436605774607487269984455892876243888859026150413831543e-01,
        -2.498464243273153794161018979207791000564669737132073715013121e-01,
        -1.959462743773770435042992543190981318766776476382778474396781e-01,
        1.273693403357932600826772332014009770786177480422245995563097e-01,
        9.305736460357235116035228983545273226942917998946925868063974e-02,
        -7.139414716639708714533609307605064767292611983702150917523756e-02,
        -2.945753682187581285828323760141839199388200516064948779769654e-02,
        3.321267405934100173976365318215912897978337413267096043323351e-02,
        3.606553566956169655423291417133403299517350518618994762730612e-03,
        -1.073317548333057504431811410651364448111548781143923213370333e-02,
        1.395351747052901165789318447957707567660542855688552426721117e-03,
        1.992405295185056117158742242640643211762555365514105280067936e-03,
        -6.858566949597116265613709819265714196625043336786920516211903e-04,
        -1.164668551292854509514809710258991891527461854347597362819235e-04,
        9.358867032006959133405013034222854399688456215297276443521873e-05,
        -1.326420289452124481243667531226683305749240960605829756400674e-05
    });
    #endregion
    #region Symlets 小波系数
    public static Wavelet Sym2 => new(nameof(Sym2), new[]
    {
        -0.12940952255092145,
        0.22414386804185735,
        0.836516303737469,
        0.48296291314469025
    });
    public static Wavelet Sym3 => new(nameof(Sym3), new[]
    {
        0.035226291882100656,
        -0.08544127388224149,
        -0.13501102001039084,
        0.4598775021193313,
        0.8068915093133388,
        0.3326705529509569
    });
    public static Wavelet Sym4 => new(nameof(Sym4), new[]
    {
        -0.07576571478927333,
        -0.02963552764599851,
        0.49761866763201545,
        0.8037387518059161,
        0.29785779560527736,
        -0.09921954357684722,
        -0.012603967262037833,
        0.0322231006040427
    });
    public static Wavelet Sym5 => new(nameof(Sym5), new[]
    {
        0.027333068345077982,
        0.029519490925774643,
        -0.039134249302383094,
        0.1993975339773936,
        0.7234076904024206,
        0.6339789634582119,
        0.01660210576452232,
        -0.17532808990845047,
        -0.021101834024758855,
        0.019538882735286728
    });
    #endregion
    #region Coiflets 小波系数 coifN
    public static Wavelet Coif1 => new(nameof(Coif1), new[]
    {
        -0.01565572813546454,
        -0.0727326195128539,
        0.38486484686420286,
        0.8525720202122554,
        0.3378976624578092,
        -0.0727326195128539
    });
    public static Wavelet Coif2 => new(nameof(Coif2), new[]
    {
        -0.0007205494453645122,
        -0.0018232088707029932,
        0.0056114348193944995,
        0.023680171946334084,
        -0.0594344186464569,
        -0.0764885990783064,
        0.41700518442169254,
        0.8127236354455423,
        0.3861100668211622,
        -0.06737255472196302,
        -0.04146493678175915,
        0.016387336463522112
    });
    public static Wavelet Coif3 => new(nameof(Coif3), new[]
    {
        -3.459977283621256e-05,
        -7.098330313814125e-05,
        0.0004662169601128863,
        0.0011175187708906016,
        -0.0025745176887502236,
        -0.00900797613666158,
        0.015880544863615904,
        0.03455502757306163,
        -0.08230192710688598,
        -0.07179982161931202,
        0.42848347637761874,
        0.7937772226256206,
        0.4051769024096169,
        -0.06112339000267287,
        -0.0657719112818555,
        0.023452696141836267,
        0.007782596427325418,
        -0.003793512864491014
    });
    public static Wavelet Coif4 => new(nameof(Coif4), new[]
    {
        -1.7849850030882614e-06,
        -3.2596802368833675e-06,
        3.1229875865345646e-05,
        6.233903446100713e-05,
        -0.00025997455248771324,
        -0.0005890207562443383,
        0.0012665619292989445,
        0.003751436157278457,
        -0.00565828668661072,
        -0.015211731527946259,
        0.025082261844864097,
        0.03933442712333749,
        -0.09622044203398798,
        -0.06662747426342504,
        0.4343860564914685,
        0.782238930920499,
        0.41530840703043026,
        -0.05607731331675481,
        -0.08126669968087875,
        0.026682300156053072,
        0.016068943964776348,
        -0.0073461663276420935,
        -0.0016294920126017326,
        0.0008923136685823146
    });
    public static Wavelet Coif5 => new(nameof(Coif5), new[]
    {
        -9.517657273819165e-08,
        -1.6744288576823017e-07,
        2.0637618513646814e-06,
        3.7346551751414047e-06,
        -2.1315026809955787e-05,
        -4.134043227251251e-05,
        0.00014054114970203437,
        0.00030225958181306315,
        -0.0006381313430451114,
        -0.0016628637020130838,
        0.0024333732126576722,
        0.006764185448053083,
        -0.009164231162481846,
        -0.01976177894257264,
        0.03268357426711183,
        0.0412892087501817,
        -0.10557420870333893,
        -0.06203596396290357,
        0.4379916261718371,
        0.7742896036529562,
        0.4215662066908515,
        -0.05204316317624377,
        -0.09192001055969624,
        0.02816802897093635,
        0.023408156785839195,
        -0.010131117519849788,
        -0.004159358781386048,
        0.0021782363581090178,
        0.00035858968789573785,
        -0.00021208083980379827
    });
    #endregion
}
