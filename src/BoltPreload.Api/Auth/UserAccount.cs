namespace BoltPreload.Api.Auth;

/// <summary>POCO，单独建一个 user 类纯粹给 PasswordHasher&lt;T&gt; 当泛型参数。</summary>
public sealed class UserAccount
{
    public long Id { get; init; }
    public string Username { get; init; } = "";
    public string Role { get; init; } = AuthRoles.Operator;
}
