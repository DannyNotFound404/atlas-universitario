using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miguel.Contracts;
using Atlas.Publisher;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
try
{
    if (args.Length == 0 || args[0] == "--help")
    {
        Console.WriteLine("ATLAS · Autorizaciones firmadas (clave privada cifrada, uso fuera del servidor)\n" +
            "bridge --distribution carpeta --version 2026.09.10.1-coordinado --key clave-cifrada.pem --out puente.atlasrelease\n" +
            "recovery --release archivo.atlasrelease --key clave-cifrada.pem --out recuperacion.json [--hours 24]\n" +
            "trust --next-public-key nueva.public.pem --generation 1 --key clave-raiz-cifrada.pem --out transicion.json [--days 90]\n" +
            "verify --authorization autorizacion.json --public-key raiz.public.pem\n" +
            "La contraseña se solicita de forma oculta. La herramienta no despliega ni activa versiones.");
        return 0;
    }
    if ((args.Length - 1) % 2 != 0) throw new ArgumentException("Los argumentos deben formar pares --opcion valor.");
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2) if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Opción repetida.");
    string Required(string name) => values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Falta " + name);
    int Number(string name, int fallback, int maximum) => !values.TryGetValue(name, out string? value) ? fallback :
        int.TryParse(value, out int result) && result >= 1 && result <= maximum ? result : throw new ArgumentException("Valor inválido: " + name);
    if (args[0] == "verify")
    {
        var envelope = JsonSerializer.Deserialize<SignedUpdaterControl>(File.ReadAllText(Required("--authorization")), json)!;
        var document = UpdaterControlValidator.Verify(envelope, File.ReadAllText(Required("--public-key")), DateTimeOffset.UtcNow);
        Console.WriteLine($"Firma y vigencia válidas · {document.Purpose} · {document.AuthorizationId} · vence {document.ExpiresAtUtc:O}"); return 0;
    }
    if (args[0] is not ("recovery" or "trust" or "bridge")) throw new ArgumentException("Operación desconocida; consulte --help.");
    string output = Path.GetFullPath(Required("--out"));
    if (File.Exists(output)) throw new IOException("El archivo de salida ya existe; use un nombre nuevo.");
    string encryptedPem = File.ReadAllText(Required("--key"));
    if (!encryptedPem.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidDataException("Se requiere una clave privada PEM cifrada de ATLAS Publisher.");
    if (Console.IsInputRedirected) throw new InvalidOperationException("Abra una terminal interactiva para introducir la contraseña de forma oculta.");
    char[] password = new char[512]; int length = 0;
    using var signer = ECDsa.Create();
    try
    {
        Console.Write("Contraseña de la clave (oculta): ");
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("Operación cancelada.");
            if (key.Key == ConsoleKey.Backspace) { if (length > 0) password[--length] = '\0'; }
            else if (!char.IsControl(key.KeyChar) && length < password.Length) password[length++] = key.KeyChar;
        }
        Console.WriteLine();
        if (args[0] == "bridge")
        {
            var narrative = new ReleaseNarrative(null, "Actualizador coordinado ATLAS 0.4: transición de la flota existente.",
                ["Descarga reanudable y coordinación con el servidor.", "Recuperación durable y confirmación posterior del agente."],
                ["Sustituye el actualizador 0.3.2; los demás paquetes conservan la base seleccionada."],
                "Conserva copias locales de recuperación de los componentes modificados. La primera transición utiliza el mecanismo del actualizador anterior.");
            var result = await ReleaseBuilder.BuildAsync(Required("--distribution"), Required("--key"), password.AsMemory(0, length),
                Required("--version"), narrative, output, ReleaseBuildProfile.UpdaterBridge, Console.WriteLine, CancellationToken.None, "0.3.2-rc.1");
            Console.WriteLine($"Versión firmada y verificada: {output}\nIdentificador: {result.ReleaseId}\nCargue la versión en ATLAS y asígnela primero a un equipo piloto.");
            return 0;
        }
        signer.ImportFromEncryptedPem(encryptedPem, password.AsSpan(0, length));
    }
    finally { Array.Clear(password); }
    string rootPem = signer.ExportSubjectPublicKeyInfoPem();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    UpdaterControlDocument control;
    if (args[0] == "recovery")
    {
        using var archive = ZipFile.OpenRead(Required("--release"));
        byte[] Read(string name)
        {
            var entries = archive.Entries.Where(e => e.FullName == name).ToArray();
            if (entries.Length != 1 || entries[0].Length > 262144) throw new InvalidDataException("Manifiesto de versión inválido.");
            using var content = entries[0].Open(); using var bytes = new MemoryStream(); content.CopyTo(bytes); return bytes.ToArray();
        }
        byte[] manifestBytes = Read("manifest.json"), signature = Read("manifest.sig");
        var keys = new List<string> { rootPem };
        if (values.TryGetValue("--release-public-key", out string? releaseKey)) keys.Add(File.ReadAllText(releaseKey));
        if (!UpdaterTrust.VerifySignature(manifestBytes, signature, keys)) throw new CryptographicException("La versión de recuperación no tiene una firma reconocida.");
        var manifest = JsonSerializer.Deserialize<AtlasReleaseManifest>(manifestBytes, json) ?? throw new InvalidDataException("Falta el manifiesto.");
        foreach (var package in manifest.Packages)
        {
            var entry = archive.GetEntry("packages/" + package.FileName) ?? throw new InvalidDataException("Falta un componente de la versión.");
            using var content = entry.Open();
            if (entry.Length != package.SizeBytes || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("El contenido del paquete no corresponde al manifiesto firmado.");
        }
        control = new(1, Guid.NewGuid(), "RECOVERY", now.AddMinutes(-5), now.AddHours(Number("--hours", 24, 8760)),
            manifest.ReleaseId, manifest.Packages.ToDictionary(p => p.Component, p => p.Sha256), null, null, 1);
    }
    else control = new(1, Guid.NewGuid(), "TRUST_TRANSITION", now.AddMinutes(-5), now.AddDays(Number("--days", 90, 365)),
        null, null, File.ReadAllText(Required("--next-public-key")), UpdaterControlValidator.Fingerprint(rootPem), Number("--generation", 1, int.MaxValue));
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(control, json);
    var signed = new SignedUpdaterControl(Convert.ToBase64String(payload), Convert.ToBase64String(signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    _ = UpdaterControlValidator.Verify(signed, rootPem, now);
    byte[] documentBytes = JsonSerializer.SerializeToUtf8Bytes(signed, json);
    using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(documentBytes); file.Flush(true); }
    Console.WriteLine($"Autorización guardada: {output}\nSHA-256: {Convert.ToHexStringLower(SHA256.HashData(documentBytes))}\nRegístrela en ATLAS; todavía no se ha ejecutado ninguna actualización."); return 0;
}
catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException or InvalidDataException or InvalidOperationException or OperationCanceledException or JsonException)
{ Console.Error.WriteLine(ex is CryptographicException ? "No se pudo verificar la clave, contraseña o firma." : ex.Message); return 1; }
