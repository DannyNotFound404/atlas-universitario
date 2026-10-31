#requires -Version 7.4
[CmdletBinding()]
param(
    [uri]$BaseUrl = 'https://localhost:5443/',
    [Security.SecureString]$Passphrase
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = Join-Path $repo '.local'
if (-not $BaseUrl.IsAbsoluteUri -or $BaseUrl.Scheme -cne 'https' -or
    $BaseUrl.UserInfo -or $BaseUrl.Query -or $BaseUrl.Fragment -or $BaseUrl.AbsolutePath -ne '/' -or
    $BaseUrl.Host -in @('0.0.0.0','::','*','+')) {
    throw 'BaseUrl debe ser una URL HTTPS raíz, con host concreto y sin credenciales, consulta ni fragmento.'
}
if (Test-Path -LiteralPath $local) {
    throw '.local ya existe. Se conservó sin sobrescribir. Use sus materiales; no regenere la confianza de un entorno existente.'
}
if (-not $Passphrase) { $Passphrase = Read-Host 'Frase nueva para proteger las claves locales y el certificado TLS (mínimo 14 caracteres)' -AsSecureString }
if ($Passphrase.Length -lt 14) { throw 'La frase debe tener al menos 14 caracteres.' }

# No se usan las autoridades, las claves ni los certificados de ninguna VM existente.
if (-not ('AtlasGitLocal.Materials' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace AtlasGitLocal {
  public static class Materials {
    static void Text(string path, string value) => File.WriteAllText(path, value + "\n", new UTF8Encoding(false));
    public static void Create(string directory, string host, string password) {
      using RSA rootKey = RSA.Create(3072);
      var rootRequest = new CertificateRequest("CN=ATLAS Git Local Evaluation CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
      rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
      rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
      rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
      var now = DateTimeOffset.UtcNow;
      using var root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(3));
      var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 250000);
      Text(Path.Combine(directory, "root-ca.crt"), root.ExportCertificatePem());
      Text(Path.Combine(directory, "root-ca.encrypted.pem"), rootKey.ExportEncryptedPkcs8PrivateKeyPem(password.AsSpan(), pbe));
      using RSA tlsKey = RSA.Create(2048);
      var leafRequest = new CertificateRequest("CN=ATLAS Git Local Evaluation", tlsKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
      leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
      leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
      var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
      leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
      var names = new SubjectAlternativeNameBuilder();
      names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
      if (!String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1") {
        if (IPAddress.TryParse(host, out var address)) names.AddIpAddress(address); else names.AddDnsName(host);
      }
      leafRequest.CertificateExtensions.Add(names.Build());
      using var leafPublic = leafRequest.Create(root, now.AddMinutes(-5), now.AddYears(2), RandomNumberGenerator.GetBytes(16));
      using var leaf = leafPublic.CopyWithPrivateKey(tlsKey);
      File.WriteAllBytes(Path.Combine(directory, "server-tls.pfx"), leaf.Export(X509ContentType.Pfx, password));
      // Publisher y Updater exigen ECDSA P-256, no RSA, para la firma de releases.
      using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
      Text(Path.Combine(directory, "release-signing.encrypted.pem"), signer.ExportEncryptedPkcs8PrivateKeyPem(password.AsSpan(), pbe));
      Text(Path.Combine(directory, "release-signing.public.pem"), signer.ExportSubjectPublicKeyInfoPem());
      byte[] challenge = RandomNumberGenerator.GetBytes(32);
      byte[] signature = signer.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
      using ECDsa verifier = ECDsa.Create();
      verifier.ImportFromPem(File.ReadAllText(Path.Combine(directory, "release-signing.public.pem")));
      if (!verifier.VerifyData(challenge, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new CryptographicException("Firma local no verificada.");
      using ECDsa decrypted = ECDsa.Create();
      decrypted.ImportFromEncryptedPem(File.ReadAllText(Path.Combine(directory, "release-signing.encrypted.pem")), password.AsSpan());
      if (!String.Equals(decrypted.ExportSubjectPublicKeyInfoPem(), signer.ExportSubjectPublicKeyInfoPem(), StringComparison.Ordinal)) throw new CryptographicException("Clave privada local no verificada.");
    }
  }
}
'@
}

[void](New-Item -ItemType Directory -Path $local)
if ($IsWindows) {
    $acl = Get-Acl -LiteralPath $local
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $local -AclObject $acl
} else {
    [IO.File]::SetUnixFileMode($local, [IO.UnixFileMode]'UserRead,UserWrite,UserExecute')
}
$pki = Join-Path $local 'pki'
[void](New-Item -ItemType Directory -Path $pki)
$plain = $null
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Passphrase)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    [AtlasGitLocal.Materials]::Create($pki, $BaseUrl.DnsSafeHost, $plain)
} finally {
    $plain = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
}
$rootCertificate = Join-Path $pki 'root-ca.crt'
$releasePublicKey = Join-Path $pki 'release-signing.public.pem'
$setupProfile = [ordered]@{
    schemaVersion = 1
    environmentName = 'UNIVERSITY_GIT'
    serverUrl = $BaseUrl.AbsoluteUri
    # Setup calcula SHA-256 de los bytes del archivo PEM, no de su representación DER.
    rootCertificateSha256 = (Get-FileHash -LiteralPath $rootCertificate -Algorithm SHA256).Hash.ToLowerInvariant()
    releasePublicKeySha256 = (Get-FileHash -LiteralPath $releasePublicKey -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $local 'setup-environment.json'), ($setupProfile | ConvertTo-Json) + "`n", [Text.UTF8Encoding]::new($false))
if (-not $IsWindows) {
    Get-ChildItem -LiteralPath $local -Recurse -Directory | ForEach-Object {
        [IO.File]::SetUnixFileMode($_.FullName, [IO.UnixFileMode]'UserRead,UserWrite,UserExecute')
    }
    Get-ChildItem -LiteralPath $local -Recurse -File | ForEach-Object {
        [IO.File]::SetUnixFileMode($_.FullName, [IO.UnixFileMode]'UserRead,UserWrite')
    }
}
Write-Output 'ATLAS_GIT_LOCAL_INITIALIZED'
Write-Output "Perfil local: $(Join-Path $local 'setup-environment.json')"
Write-Output 'Se generaron claves nuevas, cifradas y privadas. No se instalaron certificados ni componentes y no se modificó ningún servidor.'
