using System.Security.Cryptography;

namespace UrlShortener.Api;

public interface ICodeGenerator
{
    string Next();
}

/// <summary>Cryptographically random base62 codes (unguessable; 62^7 ≈ 3.5e12 combinations).</summary>
public sealed class RandomCodeGenerator : ICodeGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public int Length { get; init; } = 7;

    public string Next()
    {
        var chars = new char[Length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
