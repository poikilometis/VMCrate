using System.Security.Cryptography;
using System.Text;

namespace VMCrate.Windows;

internal static class Sha512Crypt
{
    private const string Alphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // UNIX SHA-512 crypt with a fresh salt. Debian Installer accepts the resulting $6$ password hash.
    internal static string Hash(string password) => Hash(password, RandomNumberGenerator.GetString(Alphabet, 16), 120_000);

    internal static string Hash(string password, string salt, int rounds)
    {
        var secret = Encoding.UTF8.GetBytes(password);
        var saltBytes = Encoding.ASCII.GetBytes(salt);
        byte[]? secretSequence = null;
        try
        {
            using var work = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            work.AppendData(secret);
            work.AppendData(saltBytes);
            work.AppendData(secret);
            var alternate = work.GetHashAndReset();
            work.AppendData(secret);
            work.AppendData(saltBytes);
            work.AppendData(Repeat(alternate, secret.Length));
            for (int bits = secret.Length; bits > 0; bits >>= 1)
                work.AppendData((bits & 1) != 0 ? alternate : secret);
            var digest = work.GetHashAndReset();

            for (int index = 0; index < secret.Length; index++)
                work.AppendData(secret);
            secretSequence = Repeat(work.GetHashAndReset(), secret.Length);
            for (int index = 0; index < 16 + digest[0]; index++)
                work.AppendData(saltBytes);
            var saltSequence = Repeat(work.GetHashAndReset(), saltBytes.Length);

            for (int index = 0; index < rounds; index++)
            {
                var even = (index & 1) == 0;
                work.AppendData(even ? digest : secretSequence);
                if (index % 3 != 0)
                    work.AppendData(saltSequence);
                if (index % 7 != 0)
                    work.AppendData(secretSequence);
                work.AppendData(even ? secretSequence : digest);
                work.GetHashAndReset(digest);
            }

            return $"$6$rounds={rounds}${salt}${Encode(digest)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (secretSequence is not null)
                CryptographicOperations.ZeroMemory(secretSequence);
        }
    }

    private static byte[] Repeat(byte[] bytes, int length)
    {
        var output = new byte[length];
        for (int index = 0; index < length; index++)
            output[index] = bytes[index % bytes.Length];
        return output;
    }

    private static string Encode(byte[] digest)
    {
        var output = new StringBuilder(86);
        for (int group = 0; group < 21; group++)
        {
            int first = group * 22 % 63;
            Append(digest[first], digest[(first + 21) % 63], digest[(first + 42) % 63], 4);
        }
        Append(0, 0, digest[63], 2);
        return output.ToString();

        void Append(int high, int middle, int low, int count)
        {
            var value = (high << 16) | (middle << 8) | low;
            for (int index = 0; index < count; index++, value >>= 6)
                output.Append(Alphabet[value & 63]);
        }
    }
}
