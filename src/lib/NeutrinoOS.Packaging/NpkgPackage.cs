// NeutrinoOS Phase 8 - shared packaging library (NeutrinoOS.Packaging).
//
// NpkgPackage: .npkg package build/read/verify. A package is a ZIP archive
// containing manifest.json, checksums.sha256 (SHA-256 of every payload file
// with the path "payload/<relative>"), signature.sig (Ed25519 over
// manifest.json || checksums.sha256) and the payload files themselves.

using System;
using System.Collections.Generic;
using System.Text;
using ProtonOS.DDK.Crypto;

namespace NeutrinoOS.Packaging
{
    /// <summary>
    /// Phase 8: an opened .npkg package (manifest + checksums + optional
    /// Ed25519 signature + payload ZIP). Created by Open (read path) or
    /// Build (write path); verification is explicit via VerifyChecksums and
    /// VerifySignature. Also exposes PayloadFiles/Signature aliases used by
    /// the host npkg CLI (sdk/npkg) sign flow.
    /// </summary>
    public sealed class NpkgPackage
    {
        /// <summary>Phase 8: parsed manifest.json of the package.</summary>
        public NpkgManifest Manifest;

        /// <summary>Phase 8: exact bytes of manifest.json (the signed data).</summary>
        public byte[] ManifestBytes;

        /// <summary>Phase 8: exact bytes of checksums.sha256 (the signed data).</summary>
        public byte[] ChecksumsBytes;

        /// <summary>Phase 8: bytes of signature.sig; empty when unsigned.</summary>
        public byte[] SignatureBytes;

        /// <summary>Phase 8: ZIP reader over the package (payload + metadata entries).</summary>
        public ZipReader Zip;

        /// <summary>Phase 8: alias of SignatureBytes (host npkg CLI compatibility).</summary>
        public byte[] Signature
        {
            get { return SignatureBytes; }
        }

        /// <summary>
        /// Phase 8: payload files keyed by path relative to "payload/"
        /// (sorted), ready to be passed back to Build (used by "npkg-host
        /// sign" to re-pack an opened package with a signature).
        /// </summary>
        public KeyValuePair<string, byte[]>[] PayloadFiles
        {
            get
            {
                List<KeyValuePair<string, byte[]>> files = new List<KeyValuePair<string, byte[]>>();
                string[] paths = PayloadPaths();
                for (int i = 0; i < paths.Length; i++)
                {
                    string path = paths[i];
                    string key = path;
                    if (path.Length > 8 && path.IndexOf("payload/") == 0)
                        key = path.Substring(8);
                    byte[] data = ReadPayloadFile(path);
                    if (data == null)
                        continue;
                    files.Add(new KeyValuePair<string, byte[]>(key, data));
                }
                return files.ToArray();
            }
        }

        private NpkgPackage()
        {
        }

        /// <summary>
        /// Phase 8: opens a .npkg byte image: parses the ZIP, requires
        /// manifest.json and checksums.sha256 (which may be empty for a
        /// package without payload) and accepts a missing signature.sig as
        /// "unsigned". Throws FormatException for malformed packages.
        /// </summary>
        public static NpkgPackage Open(byte[] npkg)
        {
            if (npkg == null)
                throw new ArgumentNullException("npkg");

            ZipReader zip = new ZipReader(npkg);

            ZipEntry manifestEntry = zip.Find("manifest.json");
            if (manifestEntry == null)
                throw new FormatException("npkg: package is missing manifest.json");
            byte[] manifestBytes = zip.Read(manifestEntry);

            JsonObject manifestObject = Json.Parse(Encoding.UTF8.GetString(manifestBytes)) as JsonObject;
            if (manifestObject == null)
                throw new FormatException("npkg: manifest.json must contain a JSON object");

            ZipEntry checksumsEntry = zip.Find("checksums.sha256");
            if (checksumsEntry == null)
                throw new FormatException("npkg: package is missing checksums.sha256");
            byte[] checksumsBytes = zip.Read(checksumsEntry);

            ZipEntry signatureEntry = zip.Find("signature.sig");
            byte[] signatureBytes = signatureEntry != null ? zip.Read(signatureEntry) : new byte[0];

            NpkgPackage package = new NpkgPackage();
            package.Zip = zip;
            package.ManifestBytes = manifestBytes;
            package.ChecksumsBytes = checksumsBytes;
            package.SignatureBytes = signatureBytes;
            package.Manifest = NpkgManifest.FromJson(manifestObject);
            return package;
        }

        /// <summary>
        /// Phase 8: lowercase hex SHA-256 of arbitrary data (payload
        /// checksums and key fingerprints).
        /// </summary>
        public static string Sha256Hex(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException("data");
            return TextConv.HexEncode(Sha256.Hash(data));
        }

        /// <summary>
        /// Phase 8: package signing-key fingerprint: SHA-256 of the raw
        /// 32-byte Ed25519 public key, lowercase hex (this is what the
        /// manifest "signer" field and /etc/npkg/trusted-keys store).
        /// </summary>
        public static string Fingerprint(byte[] publicKey)
        {
            if (publicKey == null)
                throw new ArgumentNullException("publicKey");
            return Sha256Hex(publicKey);
        }

        /// <summary>
        /// Phase 8: verifies every checksums.sha256 line ("hex  path",
        /// one or more spaces): the file must exist in the payload and its
        /// SHA-256 (and ZIP CRC) must match. Returns false and reports the
        /// first failing path in badPath.
        /// </summary>
        public bool VerifyChecksums(out string badPath)
        {
            badPath = "";
            List<string> hashes = new List<string>();
            List<string> paths = new List<string>();
            string malformedPath;
            if (!TryParseChecksums(ChecksumsBytes, hashes, paths, out malformedPath))
            {
                badPath = malformedPath == null ? "" : malformedPath;
                return false;
            }

            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                ZipEntry entry = Zip != null ? Zip.Find(path) : null;
                if (entry == null)
                {
                    badPath = path;
                    return false;
                }
                byte[] data;
                try
                {
                    data = Zip.Read(entry);
                }
                catch (Exception)
                {
                    badPath = path;
                    return false;
                }
                if (Sha256Hex(data) != hashes[i])
                {
                    badPath = path;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Phase 8: convenience overload of VerifyChecksums(out string) for
        /// callers that only need the boolean (host npkg CLI "verify").
        /// </summary>
        public bool VerifyChecksums()
        {
            string badPath;
            return VerifyChecksums(out badPath);
        }

        /// <summary>
        /// Phase 8: verifies the detached Ed25519 signature over
        /// manifest.json || checksums.sha256 with the given 32-byte public
        /// key. Returns false for unsigned packages (empty signature.sig),
        /// wrong keys and tampered bytes.
        /// </summary>
        public bool VerifySignature(byte[] publicKey)
        {
            if (publicKey == null || publicKey.Length == 0)
                return false;
            if (SignatureBytes == null || SignatureBytes.Length == 0)
                return false;
            byte[] message = Concat(ManifestBytes, ChecksumsBytes);
            return Ed25519.Verify(publicKey, message, SignatureBytes);
        }

        /// <summary>
        /// Phase 8: payload paths listed in checksums.sha256 ("payload/..."
        /// entries), sorted ascending.
        /// </summary>
        public string[] PayloadPaths()
        {
            List<string> hashes = new List<string>();
            List<string> paths = new List<string>();
            string malformedPath;
            TryParseChecksums(ChecksumsBytes, hashes, paths, out malformedPath);
            string[] result = paths.ToArray();
            SortStrings(result);
            return result;
        }

        /// <summary>
        /// Phase 8: raw bytes of a payload file by its archive path
        /// ("payload/name"); returns null when the path is not in the ZIP.
        /// The ZIP CRC is verified by the reader.
        /// </summary>
        public byte[] ReadPayloadFile(string path)
        {
            if (path == null || Zip == null)
                return null;
            ZipEntry entry = Zip.Find(path);
            if (entry == null)
                return null;
            return Zip.Read(entry);
        }

        /// <summary>
        /// Phase 8: builds a complete .npkg image:
        /// (1) sets manifest.Signer from the signing seed ("" for unsigned),
        /// (2) serializes the manifest (pretty JSON),
        /// (3) writes checksums.sha256 lines "hex  payload/&lt;key&gt;" sorted by
        ///     full path,
        /// (4) signs manifest.json || checksums.sha256 with Ed25519
        ///     (empty signature when signingSeed is null),
        /// (5) stores manifest.json, checksums.sha256, signature.sig and the
        ///     payload files in a deterministic stored-entry ZIP.
        /// </summary>
        public static byte[] Build(NpkgManifest manifest, KeyValuePair<string, byte[]>[] payloadFiles, byte[] signingSeed)
        {
            if (manifest == null)
                throw new ArgumentNullException("manifest");
            if (signingSeed != null && signingSeed.Length != 32)
                throw new ArgumentException("npkg: signing seed must be 32 bytes");
            if (payloadFiles == null)
                payloadFiles = new KeyValuePair<string, byte[]>[0];

            // (1) signer fingerprint (or "" for an unsigned package)
            manifest.Signer = signingSeed != null ? Fingerprint(Ed25519.PublicKeyFromSeed(signingSeed)) : "";

            // (2) manifest bytes (pretty JSON, stable field order)
            byte[] manifestBytes = Encoding.UTF8.GetBytes(Json.Write(manifest.ToJson(), true));

            // payload sorted by full path for a deterministic archive
            KeyValuePair<string, byte[]>[] sorted = CopySortedByFullPath(payloadFiles);

            // (3) checksums: "<sha256 hex>  payload/<key>\n" per file
            StringBuilder checksums = new StringBuilder();
            for (int i = 0; i < sorted.Length; i++)
            {
                string key = sorted[i].Key;
                if (key == null || key.Length == 0)
                    throw new ArgumentException("npkg: payload file keys must be non-empty");
                byte[] content = sorted[i].Value == null ? new byte[0] : sorted[i].Value;
                checksums.Append(Sha256Hex(content));
                checksums.Append("  payload/");
                checksums.Append(key);
                checksums.Append('\n');
            }
            byte[] checksumsBytes = Encoding.UTF8.GetBytes(checksums.ToString());

            // (4) detached Ed25519 signature over manifest || checksums
            byte[] signatureBytes = signingSeed != null
                ? Ed25519.Sign(signingSeed, Concat(manifestBytes, checksumsBytes))
                : new byte[0];

            // (5) ZIP: metadata first, then the sorted payload
            ZipWriter writer = new ZipWriter();
            writer.AddFile("manifest.json", manifestBytes);
            writer.AddFile("checksums.sha256", checksumsBytes);
            writer.AddFile("signature.sig", signatureBytes);
            for (int i = 0; i < sorted.Length; i++)
            {
                byte[] content = sorted[i].Value == null ? new byte[0] : sorted[i].Value;
                writer.AddFile("payload/" + sorted[i].Key, content);
            }
            return writer.Finish();
        }

        private static bool TryParseChecksums(byte[] checksums, List<string> hashes, List<string> paths, out string malformedPath)
        {
            malformedPath = null;
            if (checksums == null)
                return true;

            string text = Encoding.UTF8.GetString(checksums);
            int i = 0;
            while (i < text.Length)
            {
                int lineEnd = text.IndexOf('\n', i);
                if (lineEnd < 0)
                    lineEnd = text.Length;
                int end = lineEnd;
                if (end > i && text[end - 1] == '\r')
                    end--;

                if (end > i)
                {
                    int separator = i;
                    while (separator < end && text[separator] != ' ' && text[separator] != '\t')
                        separator++;
                    int pathStart = separator;
                    while (pathStart < end && (text[pathStart] == ' ' || text[pathStart] == '\t'))
                        pathStart++;

                    if (separator == i || pathStart >= end)
                        return false;

                    string hex = text.Substring(i, separator - i).ToLower();
                    string path = text.Substring(pathStart, end - pathStart);
                    byte[] hash = TextConv.HexDecode(hex);
                    if (hash == null || hash.Length != 32)
                    {
                        malformedPath = path;
                        return false;
                    }
                    hashes.Add(hex);
                    paths.Add(path);
                }

                if (lineEnd >= text.Length)
                    break;
                i = lineEnd + 1;
            }
            return true;
        }

        private static KeyValuePair<string, byte[]>[] CopySortedByFullPath(KeyValuePair<string, byte[]>[] files)
        {
            KeyValuePair<string, byte[]>[] result = new KeyValuePair<string, byte[]>[files.Length];
            for (int i = 0; i < files.Length; i++)
                result[i] = files[i];

            // Insertion sort by "payload/<key>" (the common prefix makes this
            // equivalent to sorting by key alone).
            for (int i = 1; i < result.Length; i++)
            {
                KeyValuePair<string, byte[]> current = result[i];
                int j = i - 1;
                while (j >= 0 && CompareOrdinal(current.Key, result[j].Key) < 0)
                {
                    result[j + 1] = result[j];
                    j--;
                }
                result[j + 1] = current;
            }
            return result;
        }

        private static void SortStrings(string[] values)
        {
            for (int i = 1; i < values.Length; i++)
            {
                string current = values[i];
                int j = i - 1;
                while (j >= 0 && CompareOrdinal(current, values[j]) < 0)
                {
                    values[j + 1] = values[j];
                    j--;
                }
                values[j + 1] = current;
            }
        }

        private static int CompareOrdinal(string a, string b)
        {
            if (a == null && b == null)
                return 0;
            if (a == null)
                return -1;
            if (b == null)
                return 1;
            int n = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i])
                    return a[i] < b[i] ? -1 : 1;
            }
            if (a.Length == b.Length)
                return 0;
            return a.Length < b.Length ? -1 : 1;
        }

        /// <summary>
        /// Phase 8 internal helper: allocates a NEW byte array holding the
        /// concatenation of the two inputs (the Ed25519 signing input must
        /// never alias the package buffers).
        /// </summary>
        private static byte[] Concat(byte[] a, byte[] b)
        {
            int lengthA = a == null ? 0 : a.Length;
            int lengthB = b == null ? 0 : b.Length;
            byte[] result = new byte[lengthA + lengthB];
            for (int i = 0; i < lengthA; i++)
                result[i] = a[i];
            for (int i = 0; i < lengthB; i++)
                result[lengthA + i] = b[i];
            return result;
        }
    }
}
