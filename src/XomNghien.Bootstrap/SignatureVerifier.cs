using System;
using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace XomNghien.Bootstrap;

internal static class SignatureVerifier
{
    public static byte[] Verify(SignedEnvelope envelope, string expectedKeyId, string publicKeyPath)
    {
        if (!string.Equals(envelope.Algorithm, "RS256", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported bootstrap signature algorithm");
        if (!string.Equals(envelope.KeyId, expectedKeyId, StringComparison.Ordinal))
            throw new InvalidDataException("Bootstrap manifest was signed by an unexpected key");
        var payload = Convert.FromBase64String(envelope.Payload);
        var signature = Convert.FromBase64String(envelope.Signature);
        using var rsa = RSA.Create();
        rsa.ImportParameters(ReadXmlKey(File.ReadAllText(publicKeyPath)));
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("Bootstrap manifest signature is invalid");
        return payload;
    }

    internal static RSAParameters ReadXmlKey(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("RSA public key XML is empty");
        var modulus = root.Element("Modulus")?.Value;
        var exponent = root.Element("Exponent")?.Value;
        if (string.IsNullOrWhiteSpace(modulus) || string.IsNullOrWhiteSpace(exponent))
            throw new InvalidDataException("RSA public key XML is invalid");
        return new RSAParameters { Modulus = Convert.FromBase64String(modulus), Exponent = Convert.FromBase64String(exponent) };
    }
}
