namespace BackendLoyalty.Infrastructure.Members;

internal static class ScryptEncoder
{
    public static byte[] CryptoScrypt(
        byte[] password,
        byte[] salt,
        int n,
        int r,
        int p,
        int keyLength = 32)
    {
        return Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(
            password,
            salt,
            n,
            r,
            p,
            keyLength);
    }
}
