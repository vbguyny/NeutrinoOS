// NeutrinoOS Phase 8 - host-side self test for NeutrinoOS.Packaging.
//
// Runs on desktop .NET (net10.0) and exercises TextConv, Json, SemVer,
// Inflate, Zip, Manifest, NpkgPackage and Repository, plus the DDK Ed25519
// implementation against the RFC 8032 TEST 1 vector.  Prints one
// [PASS]/[FAIL] line per check and a final summary; exits nonzero when any
// check failed.
//
//   dotnet run -c Release

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NeutrinoOS.Packaging;
using ProtonOS.DDK.Crypto;

namespace NeutrinoOS.Packaging.SelfTest
{
    internal static class Program
    {
        private static int _pass;
        private static int _fail;

        private static int Main()
        {
            TestTextConv();
            TestJson();
            TestSemVer();
            TestInflateAndZip();
            TestManifest();
            TestNpkgPackage();
            TestRepository();
            TestEd25519Rfc8032();

            Console.WriteLine("=== selftest summary: " + _pass + " PASS / " + _fail + " FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ 1. TextConv

        private static void TestTextConv()
        {
            long[] values = new long[]
            {
                0, 1, -1, 7, -7, 42, 1000, -1000, 1234567890L, -1234567890L,
                9007199254740993L, -9007199254740993L, 9223372036854775806L,
                long.MaxValue, long.MinValue
            };

            bool ok = true;
            for (int i = 0; i < values.Length; i++)
            {
                if (TextConv.LongToString(values[i]) != values[i].ToString())
                    ok = false;
            }
            Check("TextConv.LongToString matches long.ToString", ok);

            ok = true;
            for (int i = 0; i < values.Length; i++)
            {
                long parsed;
                if (!TextConv.TryParseLong(TextConv.LongToString(values[i]), out parsed) || parsed != values[i])
                    ok = false;
            }
            Check("TextConv.LongToString/TryParseLong roundtrip", ok);

            string[] malformed = new string[]
            {
                "", "-", "12a", "+5", " 12", "1.5", "9223372036854775808", "-9223372036854775809"
            };
            ok = true;
            for (int i = 0; i < malformed.Length; i++)
            {
                long parsed;
                if (TextConv.TryParseLong(malformed[i], out parsed))
                    ok = false;
            }
            Check("TextConv.TryParseLong rejects malformed/overflow", ok);

            long minValue;
            Check("TextConv.TryParseLong parses long.MinValue",
                TextConv.TryParseLong("-9223372036854775808", out minValue) && minValue == long.MinValue);

            byte[] sample = new byte[256];
            for (int i = 0; i < 256; i++)
                sample[i] = (byte)i;
            string hex = TextConv.HexEncode(sample);
            Check("TextConv.HexEncode is lowercase", hex.Substring(0, 4) == "0001" && hex.Substring(510) == "ff");
            byte[] roundtrip = TextConv.HexDecode(hex);
            Check("TextConv hex roundtrip", roundtrip != null && BytesEqual(roundtrip, sample));
            Check("TextConv.HexDecode accepts uppercase", BytesEqual(TextConv.HexDecode("AB"), new byte[] { 0xAB }));
            Check("TextConv.HexDecode invalid -> null", TextConv.HexDecode("zz") == null && TextConv.HexDecode("abc") == null);

            byte[] crcSample = Encoding.ASCII.GetBytes("123456789");
            Check("TextConv.Crc32 known vector", TextConv.Crc32(crcSample, 0, crcSample.Length) == 0xCBF43926u);
            Check("TextConv.Crc32 empty input", TextConv.Crc32(new byte[0], 0, 0) == 0u);
        }

        // ---------------------------------------------------------------- 2. Json

        private static void TestJson()
        {
            string text = "{\"name\":\"npkg\",\"count\":3,\"pi\":3.5,\"ok\":true,\"none\":null,"
                + "\"nested\":{\"a\":[1,2,\"three\"]},\"esc\":\"line\\nquote\\\"tab\\tback\\\\\","
                + "\"uni\":\"A\\u00e9B\"}";
            object parsed = Json.Parse(text);
            JsonObject o = parsed as JsonObject;
            Check("Json.Parse returns a JsonObject", o != null);
            Check("Json string value", o.GetString("name") == "npkg");
            Check("Json long value", o.GetLong("count", -1) == 3);
            Check("Json double value", o.Get("pi") is double && (double)o.Get("pi") == 3.5);
            Check("Json bool value", o.GetBool("ok", false));
            Check("Json null value present", o.Has("none") && o.Get("none") == null);
            Check("Json GetLong default on wrong type", o.GetLong("name", -1) == -1);

            JsonObject nested = o.GetObject("nested");
            JsonArray inner = nested != null ? nested.GetArray("a") : null;
            Check("Json nested object/array", inner != null && inner.Count == 3);
            Check("Json array element", inner.Get(2) as string == "three");
            Check("Json escape sequences", o.GetString("esc") == "line\nquote\"tab\tback\\");
            Check("Json unicode escape", o.GetString("uni") == "A\u00e9B");

            JsonObject longWrap = Json.Parse("{\"v\":123}") as JsonObject;
            Check("Json integral number parses as long", longWrap.Get("v") is long && (long)longWrap.Get("v") == 123L);
            JsonObject bigWrap = Json.Parse("{\"v\":9223372036854775808}") as JsonObject;
            Check("Json long overflow falls back to double", bigWrap.Get("v") is double);
            JsonArray topArray = Json.Parse("[1,\"two\",false]") as JsonArray;
            Check("Json top-level array", topArray != null && topArray.Count == 3);

            // Write -> Parse roundtrip on a mixed tree
            JsonObject root = new JsonObject();
            root.Set("s", "hello \"world\"\n");
            root.Set("n", 42L);
            root.Set("neg", -7L);
            root.Set("b", true);
            root.Set("z", null);
            JsonArray arr = new JsonArray();
            arr.Add(1L);
            arr.Add("x");
            arr.Add(false);
            root.Set("arr", arr);
            JsonObject sub = new JsonObject();
            sub.Set("k", "v");
            root.Set("obj", sub);

            string compact = Json.Write(root);
            Check("Json compact write/parse roundtrip", Json.Write(Json.Parse(compact)) == compact);
            string pretty = Json.Write(root, true);
            Check("Json pretty write parses to the same tree", Json.Write(Json.Parse(pretty)) == compact);
            Check("Json pretty uses 2-space indentation", pretty.IndexOf("\n  \"s\"") >= 0);
            Check("Json.EscapeString", Json.EscapeString("a\"b") == "\"a\\\"b\"");
            Check("Json.Set replaces value", ReplaceKeepsOrder());

            JsonObject numbers = new JsonObject();
            numbers.Set("whole", 1.0);
            numbers.Set("frac", 1.5);
            numbers.Set("small", 0.25);
            numbers.Set("neg", -2.5);
            string numberText = Json.Write(numbers);
            Check("Json double formatting", numberText.IndexOf("\"whole\":1") >= 0
                && numberText.IndexOf("\"frac\":1.5") >= 0
                && numberText.IndexOf("\"small\":0.25") >= 0
                && numberText.IndexOf("\"neg\":-2.5") >= 0);

            string[] bad = new string[]
            {
                "", "  ", "{", "[1,", "{\"a\":}", "{\"a\":1,}", "[1,]", "tru", "\"unterminated",
                "01", "-", "{\"a\" 1}", "{'a':1}", "1 2", "{\"a\":1}}"
            };
            bool ok = true;
            for (int i = 0; i < bad.Length; i++)
            {
                try
                {
                    Json.Parse(bad[i]);
                    ok = false;
                }
                catch (FormatException)
                {
                }
            }
            Check("Json malformed input throws FormatException", ok);
        }

        private static bool ReplaceKeepsOrder()
        {
            JsonObject o = new JsonObject();
            o.Set("a", 1L);
            o.Set("b", 2L);
            o.Set("a", 9L);
            string[] keys = o.Keys();
            return keys.Length == 2 && keys[0] == "a" && keys[1] == "b"
                && o.GetLong("a", -1) == 9 && o.Count == 2;
        }

        // -------------------------------------------------------------- 3. SemVer

        private static void TestSemVer()
        {
            Check("SemVer parse fields", ParseCheck("1.2.3", 1, 2, 3, null));
            Check("SemVer parse prerelease", ParseCheck("1.2.3-rc.1", 1, 2, 3, "rc.1"));
            Check("SemVer ToString roundtrip", SemVersion.Parse("1.2.3-rc.1").ToString() == "1.2.3-rc.1"
                && SemVersion.Parse("2.0.0").ToString() == "2.0.0");

            SemVersion tmp;
            Check("SemVer rejects partial versions", !SemVersion.TryParse("1.2", out tmp) && !SemVersion.TryParse("1", out tmp));
            Check("SemVer rejects extra components", !SemVersion.TryParse("1.2.3.4", out tmp));
            Check("SemVer rejects garbage", !SemVersion.TryParse("1.2.x", out tmp) && !SemVersion.TryParse("", out tmp));

            Check("SemVer compare 1.2.3 < 1.2.4", Cmp("1.2.3", "1.2.4") < 0);
            Check("SemVer compare 1.2.3 < 1.3.0", Cmp("1.2.3", "1.3.0") < 0);
            Check("SemVer compare 2.0.0 > 1.9.9", Cmp("2.0.0", "1.9.9") > 0);
            Check("SemVer prerelease < release", Cmp("1.0.0-alpha", "1.0.0") < 0);
            Check("SemVer alpha < alpha.1", Cmp("1.0.0-alpha", "1.0.0-alpha.1") < 0);
            Check("SemVer alpha.1 < alpha.beta", Cmp("1.0.0-alpha.1", "1.0.0-alpha.beta") < 0);
            Check("SemVer rc.1 < rc.2", Cmp("1.2.3-rc.1", "1.2.3-rc.2") < 0);
            Check("SemVer numeric identifiers compare numerically", Cmp("1.0.0-2", "1.0.0-10") < 0);
            Check("SemVer equality", SemVersion.Parse("1.2.3").Equals(SemVersion.Parse("1.2.3"))
                && !SemVersion.Parse("1.2.3").Equals(SemVersion.Parse("1.2.4")));

            ConstraintCase("=1.2.3", "1.2.3", true);
            ConstraintCase("=1.2.3", "1.2.4", false);
            ConstraintCase("==1.2.3", "1.2.3", true);
            ConstraintCase("1.2.3", "1.2.3", true);
            ConstraintCase(">=1.0.0", "1.0.0", true);
            ConstraintCase(">=1.0.0", "0.9.9", false);
            ConstraintCase(">=1.0.0 <2.0.0", "1.5.0", true);
            ConstraintCase(">=1.0.0 <2.0.0", "2.0.0", false);
            ConstraintCase(">=1.0.0 <2.0.0", "0.9.0", false);
            ConstraintCase(">=1.0.0, <2.0.0", "1.5.0", true);
            ConstraintCase(">1.0.0", "1.0.1", true);
            ConstraintCase(">1.0.0", "1.0.0", false);
            ConstraintCase("<=2.0.0", "2.0.0", true);
            ConstraintCase("<2.0.0", "1.9.9", true);
            ConstraintCase("~1.2.3", "1.2.3", true);
            ConstraintCase("~1.2.3", "1.2.9", true);
            ConstraintCase("~1.2.3", "1.3.0", false);
            ConstraintCase("~1.2.3", "1.2.2", false);
            ConstraintCase("~1.2", "1.2.7", true);
            ConstraintCase("~1.2", "1.3.0", false);
            ConstraintCase("^1.2.3", "1.2.3", true);
            ConstraintCase("^1.2.3", "1.9.9", true);
            ConstraintCase("^1.2.3", "2.0.0", false);
            ConstraintCase("^0.2.3", "0.2.9", true);
            ConstraintCase("^0.2.3", "0.3.0", false);
            ConstraintCase("^0.0.3", "0.0.3", true);
            ConstraintCase("^0.0.3", "0.0.4", false);
            ConstraintCase("=1.0.0-alpha", "1.0.0-alpha", true);
            ConstraintCase(">=1.0.0", "1.0.0-alpha", false);
            ConstraintCase("~1.2.3", "1.2.4-rc.1", false);
            ConstraintCase("^1.2.3", "1.3.0-rc.1", false);

            VersionConstraint invalid;
            Check("VersionConstraint rejects garbage", !VersionConstraint.TryParse("~x", out invalid)
                && !VersionConstraint.TryParse("", out invalid) && !VersionConstraint.TryParse(null, out invalid));

            VersionConstraint range;
            VersionConstraint.TryParse(">=1.0.0 <2.0.0", out range);
            Check("VersionConstraint ToString", range != null && range.ToString() == ">=1.0.0 <2.0.0");
            VersionConstraint tilde;
            VersionConstraint.TryParse("~1.2.3", out tilde);
            Check("VersionConstraint canonical tilde text", tilde != null && tilde.ToString() == ">=1.2.3 <1.3.0");
        }

        private static bool ParseCheck(string text, int major, int minor, int patch, string preRelease)
        {
            SemVersion version;
            if (!SemVersion.TryParse(text, out version))
                return false;
            return version.Major == major && version.Minor == minor && version.Patch == patch
                && version.PreRelease == preRelease;
        }

        private static int Cmp(string a, string b)
        {
            return SemVersion.Parse(a).CompareTo(SemVersion.Parse(b));
        }

        private static void ConstraintCase(string constraintText, string versionText, bool expected)
        {
            VersionConstraint constraint;
            bool parsed = VersionConstraint.TryParse(constraintText, out constraint);
            bool matched = parsed && constraint.Matches(SemVersion.Parse(versionText));
            Check("VersionConstraint '" + constraintText + "' vs " + versionText + " => "
                + (expected ? "match" : "no match"), parsed && matched == expected);
        }

        // --------------------------------------------------------- 4. Inflate/Zip

        private static void TestInflateAndZip()
        {
            // raw DEFLATE through our own Inflate (exact and grow modes)
            byte[] compressible = Encoding.UTF8.GetBytes(BuildCompressibleText());
            byte[] compressed = Deflate(compressible);
            byte[] inflatedExact = Inflate.Raw(compressed, 0, compressed.Length, compressible.Length);
            Check("Inflate.Raw exact-size mode", BytesEqual(inflatedExact, compressible));
            byte[] inflatedGrow = Inflate.Raw(compressed, 0, compressed.Length, -1);
            Check("Inflate.Raw grow mode", BytesEqual(inflatedGrow, compressible));
            // minimal raw DEFLATE stream: one final fixed-Huffman block with just EOB
            byte[] emptyStream = new byte[] { 0x03, 0x00 };
            byte[] inflatedZero = Inflate.Raw(emptyStream, 0, emptyStream.Length, 0);
            Check("Inflate.Raw empty stream", inflatedZero.Length == 0);

            // ZIP archive produced by System.IO.Compression (deflate) read by ZipReader
            byte[] randomish = BuildPseudoRandom(100000);
            byte[] textBytes = Encoding.UTF8.GetBytes(BuildCompressibleText());
            byte[] empty = new byte[0];

            byte[] archive;
            using (MemoryStream ms = new MemoryStream())
            {
                using (ZipArchive za = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    AddZipEntry(za, "bin/data.bin", randomish, CompressionLevel.Optimal);
                    AddZipEntry(za, "docs/readme.txt", textBytes, CompressionLevel.Optimal);
                    AddZipEntry(za, "empty.txt", empty, CompressionLevel.Optimal);
                }
                archive = ms.ToArray();
            }

            ZipReader dotnetReader = new ZipReader(archive);
            Check("ZipReader reads .NET archive", dotnetReader.Entries().Length == 3);
            ZipEntry readmeEntry = dotnetReader.Find("docs/readme.txt");
            Check("ZipReader .NET text entry (deflate)", readmeEntry != null && BytesEqual(dotnetReader.Read(readmeEntry), textBytes));
            ZipEntry binEntry = dotnetReader.Find("bin/data.bin");
            Check("ZipReader .NET binary entry", binEntry != null && BytesEqual(dotnetReader.Read(binEntry), randomish));
            ZipEntry emptyEntry = dotnetReader.Find("empty.txt");
            Check("ZipReader .NET empty entry", emptyEntry != null && dotnetReader.Read(emptyEntry).Length == 0);

            // deterministic stored writer -> reader roundtrip
            ZipWriter writer = new ZipWriter();
            writer.AddFile("a.txt", textBytes);
            writer.AddFile("bin/data.bin", randomish);
            writer.AddDirectory("dirlike");
            byte[] built = writer.Finish();
            byte[] builtAgain = writer.Finish();
            Check("ZipWriter.Finish is deterministic", BytesEqual(built, builtAgain));

            ZipReader storedReader = new ZipReader(built);
            Check("ZipWriter entries roundtrip", storedReader.Entries().Length == 3);
            Check("ZipWriter text roundtrip", BytesEqual(storedReader.Read(storedReader.Find("a.txt")), textBytes));
            Check("ZipWriter binary roundtrip", BytesEqual(storedReader.Read(storedReader.Find("bin/data.bin")), randomish));
            ZipEntry dirEntry = storedReader.Find("dirlike");
            Check("ZipWriter directory lookup via name + '/'", dirEntry != null && dirEntry.IsDirectory);

            // flip one stored payload byte -> CRC mismatch on Read
            byte[] tampered = CopyOf(built);
            ZipEntry aEntry = storedReader.Find("a.txt");
            int tamperAt = (int)aEntry.LocalHeaderOffset + 30 + ReadU16At(tampered, (int)aEntry.LocalHeaderOffset + 26)
                + ReadU16At(tampered, (int)aEntry.LocalHeaderOffset + 28);
            tampered[tamperAt] = (byte)(tampered[tamperAt] ^ 0xFF);
            ZipReader tamperedReader = new ZipReader(tampered);
            CheckThrows("ZipReader detects CRC mismatch", () => tamperedReader.Read(tamperedReader.Find("a.txt")));

            // unsupported compression method is reported clearly
            byte[] fakeMethod = CopyOf(built);
            PatchMethod(fakeMethod, storedReader.Find("a.txt"), 99);
            ZipReader fakeReader = new ZipReader(fakeMethod);
            CheckThrows("ZipReader rejects unsupported methods", () => fakeReader.Read(fakeReader.Find("a.txt")));
        }

        private static void PatchMethod(byte[] archive, ZipEntry entry, int method)
        {
            // local file header: signature(4) version(2) flags(2) method(2)
            int localAt = (int)entry.LocalHeaderOffset + 8;
            archive[localAt] = (byte)method;
            archive[localAt + 1] = 0;

            // central directory: walk entries from the EOCD to find this name
            int eocd = archive.Length - 22;
            long centralOffset = (long)(archive[eocd + 16] & 0xFF) | ((long)(archive[eocd + 17] & 0xFF) << 8)
                | ((long)(archive[eocd + 18] & 0xFF) << 16) | ((long)(archive[eocd + 19] & 0xFF) << 24);
            int total = (archive[eocd + 10] & 0xFF) | ((archive[eocd + 11] & 0xFF) << 8);
            byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Name);
            int at = (int)centralOffset;
            for (int i = 0; i < total; i++)
            {
                int nameLength = (archive[at + 28] & 0xFF) | ((archive[at + 29] & 0xFF) << 8);
                int extraLength = (archive[at + 30] & 0xFF) | ((archive[at + 31] & 0xFF) << 8);
                int commentLength = (archive[at + 32] & 0xFF) | ((archive[at + 33] & 0xFF) << 8);
                if (nameLength == nameBytes.Length)
                {
                    bool match = true;
                    for (int k = 0; k < nameLength; k++)
                    {
                        if (archive[at + 46 + k] != nameBytes[k])
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match)
                    {
                        archive[at + 10] = (byte)method;
                        archive[at + 11] = 0;
                        return;
                    }
                }
                at += 46 + nameLength + extraLength + commentLength;
            }
        }

        private static void AddZipEntry(ZipArchive archive, string name, byte[] data, CompressionLevel level)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, level);
            using (Stream stream = entry.Open())
                stream.Write(data, 0, data.Length);
        }

        private static byte[] Deflate(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (DeflateStream ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    ds.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private static byte[] BuildPseudoRandom(int length)
        {
            byte[] data = new byte[length];
            uint state = 0x12345678u;
            for (int i = 0; i < length; i++)
            {
                state = state * 1664525u + 1013904223u;
                data[i] = (byte)(state >> 24);
            }
            return data;
        }

        private static string BuildCompressibleText()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 400; i++)
                sb.Append("line ").Append(i.ToString()).Append(": the quick brown fox jumps over the lazy dog\n");
            return sb.ToString();
        }

        private static int ReadU16At(byte[] data, int offset)
        {
            return (data[offset] & 0xFF) | ((data[offset + 1] & 0xFF) << 8);
        }

        // ------------------------------------------------------------ 5. Manifest

        private static void TestManifest()
        {
            NpkgManifest manifest = new NpkgManifest();
            manifest.Name = "tests.self.hello";
            manifest.Version = SemVersion.Parse("1.2.3");
            manifest.Architecture = "x86-64";
            manifest.Author = "selftest";
            manifest.Description = "self test package";
            manifest.License = "MIT";
            manifest.Homepage = "http://example.invalid/";
            manifest.InstallPath = "/apps/hello";
            manifest.Provides.Add("application");
            manifest.Provides.Add("utility");
            manifest.Dependencies["tests.base"] = ">=1.0.0";
            manifest.Dependencies["tests.extra"] = "~2.1";
            manifest.EntryPoints["hello"] = "hello.dll";
            manifest.Scripts["post-install"] = "echo installed";
            manifest.Driver = new NpkgDriverInfo();
            manifest.Driver.Class = "net";
            manifest.Driver.VendorIds = new string[] { "0x1af4" };
            manifest.Driver.DeviceIds = new string[] { "0x1000", "0x1001" };
            manifest.Driver.EntryPoint = "netdrv.dll";

            string text = Json.Write(manifest.ToJson(), true);
            NpkgManifest parsed = NpkgManifest.FromJson(Json.Parse(text));
            Check("Manifest roundtrip name/version", parsed.Name == manifest.Name && parsed.Version.Equals(manifest.Version));
            Check("Manifest roundtrip architecture/author/license", parsed.Architecture == "x86-64"
                && parsed.Author == "selftest" && parsed.License == "MIT");
            Check("Manifest roundtrip homepage/installPath", parsed.Homepage == manifest.Homepage
                && parsed.InstallPath == "/apps/hello");
            Check("Manifest roundtrip provides", parsed.Provides.Count == 2 && parsed.Provides[0] == "application"
                && parsed.ProvidesCapability("utility"));
            Check("Manifest roundtrip dependencies", parsed.Dependencies["tests.base"] == ">=1.0.0"
                && parsed.Dependencies["tests.extra"] == "~2.1");
            Check("Manifest roundtrip entryPoints", parsed.EntryPoints["hello"] == "hello.dll");
            Check("Manifest roundtrip scripts", parsed.Scripts["post-install"] == "echo installed");
            Check("Manifest roundtrip driver", parsed.Driver != null && parsed.Driver.Class == "net"
                && parsed.Driver.VendorIds.Length == 1 && parsed.Driver.VendorIds[0] == "0x1af4"
                && parsed.Driver.DeviceIds.Length == 2 && parsed.Driver.EntryPoint == "netdrv.dll");
            Check("Manifest stable re-serialization", Json.Write(parsed.ToJson(), true) == text);

            string minimal = "{\"name\":\"tests.hello-utility\",\"version\":\"1.0.0\",\"architecture\":\"any\","
                + "\"provides\":[\"utility\"],\"entryPoints\":{\"helloutil\":\"helloutil.dll\"},"
                + "\"installPath\":\"/bin\",\"signer\":\"\",\"unknownField\":123}";
            NpkgManifest fixture = NpkgManifest.FromJson(Json.Parse(minimal));
            Check("Manifest lenient defaults for missing fields", fixture.Format == "npkg/1"
                && fixture.Name == "tests.hello-utility" && fixture.InstallPath == "/bin"
                && fixture.EntryPoints["helloutil"] == "helloutil.dll" && fixture.Driver == null
                && fixture.Author == "" && fixture.ProvidesCapability("utility"));
            Check("Manifest ProvidesCapability negative", !fixture.ProvidesCapability("driver"));

            string driverText = "{\"name\":\"tests.hello-driver\",\"version\":\"1.0.0\",\"provides\":[\"driver\"],"
                + "\"driver\":{\"class\":\"virtual\",\"vendorIds\":[],\"deviceIds\":[],\"entryPoint\":\"hellodrv.dll\"}}";
            NpkgManifest driverManifest = NpkgManifest.FromJson(Json.Parse(driverText));
            Check("Manifest driver fixture", driverManifest.Driver != null && driverManifest.Driver.Class == "virtual"
                && driverManifest.Driver.VendorIds.Length == 0 && driverManifest.Driver.EntryPoint == "hellodrv.dll");

            CheckThrows("Manifest rejects wrong format", () => NpkgManifest.FromJson(Json.Parse("{\"format\":\"npkg/9\"}")));
            CheckThrows("Manifest rejects invalid version", () => NpkgManifest.FromJson(Json.Parse("{\"version\":\"nope\"}")));
        }

        // --------------------------------------------------------- 6. NpkgPackage

        private static void TestNpkgPackage()
        {
            byte[] seed = Hex("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
            byte[] publicKey = Ed25519.PublicKeyFromSeed(seed);

            NpkgManifest manifest = new NpkgManifest();
            manifest.Name = "tests.self.pkg";
            manifest.Version = SemVersion.Parse("1.0.0");
            manifest.Description = "npkg self test";
            manifest.Provides.Add("utility");
            manifest.EntryPoints["self"] = "self.dll";

            byte[] dllBytes = new byte[2048];
            for (int i = 0; i < dllBytes.Length; i++)
                dllBytes[i] = (byte)(i * 31 + 7);
            byte[] dataBytes = Encoding.UTF8.GetBytes("{\"setting\":true}\n");

            KeyValuePair<string, byte[]>[] files = new KeyValuePair<string, byte[]>[]
            {
                Kv("self.dll", dllBytes),
                Kv("data/x.json", dataBytes)
            };

            byte[] packageBytes = NpkgPackage.Build(manifest, files, seed);
            Check("NpkgPackage.Build sets signer fingerprint", manifest.Signer == NpkgPackage.Fingerprint(publicKey));

            NpkgPackage package = NpkgPackage.Open(packageBytes);
            Check("NpkgPackage.Open manifest", package.Manifest.Name == "tests.self.pkg"
                && package.Manifest.Version.Equals(manifest.Version));
            Check("NpkgPackage.VerifyChecksums", package.VerifyChecksums());
            string badPath;
            Check("NpkgPackage.VerifyChecksums(out) reports success", package.VerifyChecksums(out badPath) && badPath == "");
            Check("NpkgPackage.VerifySignature", package.VerifySignature(publicKey));
            Check("NpkgPackage.Signature alias", package.Signature != null && package.Signature.Length == 64);

            byte[] otherKey = Ed25519.PublicKeyFromSeed(BuildPseudoRandom(32));
            Check("NpkgPackage rejects wrong key", !package.VerifySignature(otherKey));

            string[] paths = package.PayloadPaths();
            Check("NpkgPackage.PayloadPaths sorted", paths.Length == 2
                && paths[0] == "payload/data/x.json" && paths[1] == "payload/self.dll");
            Check("NpkgPackage.ReadPayloadFile", BytesEqual(package.ReadPayloadFile("payload/self.dll"), dllBytes));
            Check("NpkgPackage.ReadPayloadFile missing -> null", package.ReadPayloadFile("payload/nope") == null);

            byte[] rebuilt = NpkgPackage.Build(package.Manifest, package.PayloadFiles, seed);
            Check("NpkgPackage rebuild is byte-identical", BytesEqual(rebuilt, packageBytes));
            Check("NpkgPackage.PayloadFiles keys are payload-relative", package.PayloadFiles.Length == 2
                && package.PayloadFiles[0].Key == "data/x.json");

            // tamper one payload byte -> checksum failure on that exact path
            byte[] tampered = CopyOf(packageBytes);
            int at = IndexOfBytes(tampered, dllBytes);
            Check("NpkgPackage tamper target located", at >= 0);
            if (at >= 0)
                tampered[at + 10] = (byte)(tampered[at + 10] ^ 0x5A);
            NpkgPackage tamperedPackage = NpkgPackage.Open(tampered);
            string tamperedBadPath;
            bool tamperedOk = tamperedPackage.VerifyChecksums(out tamperedBadPath);
            Check("NpkgPackage tampered payload detected", !tamperedOk && tamperedBadPath == "payload/self.dll");

            // tamper manifest.json but keep the old signature -> signature failure
            NpkgManifest changedManifest = NpkgManifest.FromJson(Json.Parse(Encoding.UTF8.GetString(package.ManifestBytes)));
            changedManifest.Description = changedManifest.Description + " tampered";
            byte[] changedManifestBytes = Encoding.UTF8.GetBytes(Json.Write(changedManifest.ToJson(), true));
            ZipWriter rewriter = new ZipWriter();
            rewriter.AddFile("manifest.json", changedManifestBytes);
            rewriter.AddFile("checksums.sha256", package.ChecksumsBytes);
            rewriter.AddFile("signature.sig", package.SignatureBytes);
            for (int i = 0; i < files.Length; i++)
                rewriter.AddFile("payload/" + files[i].Key, files[i].Value);
            NpkgPackage changedPackage = NpkgPackage.Open(rewriter.Finish());
            Check("NpkgPackage tampered manifest signature rejected", !changedPackage.VerifySignature(publicKey));
            Check("NpkgPackage tampered manifest checksums still pass", changedPackage.VerifyChecksums());

            // unsigned package
            NpkgManifest unsignedManifest = new NpkgManifest();
            unsignedManifest.Name = "tests.self.unsigned";
            unsignedManifest.Version = SemVersion.Parse("0.1.0");
            byte[] unsignedBytes = NpkgPackage.Build(unsignedManifest, files, null);
            NpkgPackage unsignedPackage = NpkgPackage.Open(unsignedBytes);
            Check("NpkgPackage unsigned signer is empty", unsignedPackage.Manifest.Signer == ""
                && unsignedPackage.SignatureBytes.Length == 0);
            Check("NpkgPackage unsigned VerifySignature false", !unsignedPackage.VerifySignature(publicKey));
            Check("NpkgPackage unsigned checksums pass", unsignedPackage.VerifyChecksums());

            // empty payload package: checksums.sha256 is present but empty
            NpkgManifest emptyManifest = new NpkgManifest();
            emptyManifest.Name = "tests.self.empty";
            emptyManifest.Version = SemVersion.Parse("0.0.1");
            byte[] emptyPackageBytes = NpkgPackage.Build(emptyManifest, new KeyValuePair<string, byte[]>[0], seed);
            NpkgPackage emptyPackage = NpkgPackage.Open(emptyPackageBytes);
            Check("NpkgPackage empty payload", emptyPackage.ChecksumsBytes.Length == 0
                && emptyPackage.VerifyChecksums() && emptyPackage.PayloadPaths().Length == 0
                && emptyPackage.VerifySignature(publicKey));

            CheckThrows("NpkgPackage.Open rejects non-ZIP input", () => NpkgPackage.Open(new byte[] { 1, 2, 3, 4 }));
        }

        // ---------------------------------------------------------- 7. Repository

        private static void TestRepository()
        {
            RepoPackageInfo alpha1 = MakeRepoInfo("alpha.pkg", "1.0.0", "alpha-1.0.0.npkg");
            RepoPackageInfo alpha2 = MakeRepoInfo("alpha.pkg", "1.5.0", "alpha-1.5.0.npkg");
            RepoPackageInfo beta = MakeRepoInfo("beta.pkg", "2.0.0", "beta-2.0.0.npkg");
            alpha1.Signer = "aa11";
            alpha1.Dependencies["alpha-base"] = ">=0.5.0";
            alpha1.Provides = new string[] { "utility" };

            RepositoryIndex index = new RepositoryIndex();
            index.Name = "local";
            index.Revision = 3;
            index.Generated = "self-test";
            index.Packages = new RepoPackageInfo[] { beta, alpha1, alpha2 }; // array-assignment compatibility
            index.Packages.Add(MakeRepoInfo("gamma.pkg", "0.9.0", "gamma-0.9.0.npkg"));

            string text = Json.Write(index.ToJson(), true);
            RepositoryIndex parsed = RepositoryIndex.FromJson(Json.Parse(text));
            Check("RepositoryIndex roundtrip", parsed.Name == "local" && parsed.Revision == 3
                && parsed.Generated == "self-test" && parsed.Packages.Count == 4);
            Check("RepositoryIndex sorts by name then version", parsed.Packages[0].Name == "alpha.pkg"
                && parsed.Packages[0].Version.Equals(SemVersion.Parse("1.0.0"))
                && parsed.Packages[1].Version.Equals(SemVersion.Parse("1.5.0"))
                && parsed.Packages[2].Name == "beta.pkg" && parsed.Packages[3].Name == "gamma.pkg");
            Check("RepositoryIndex stable re-serialization", Json.Write(parsed.ToJson(), true) == text);
            Check("RepoPackageInfo roundtrip", parsed.Packages[3].Filename == "gamma-0.9.0.npkg"
                && parsed.Packages[0].Dependencies["alpha-base"] == ">=0.5.0"
                && parsed.Packages[0].Provides.Length == 1 && parsed.Packages[0].Provides[0] == "utility"
                && parsed.Packages[0].Signer == "aa11");
            Check("RepoPackageInfo.File alias reads", parsed.Packages[3].File == "gamma-0.9.0.npkg");

            RepoPackageInfo alias = new RepoPackageInfo();
            alias.Name = "alias.pkg";
            alias.Version = SemVersion.Parse("1.0.0");
            alias.File = "alias.npkg"; // host npkg CLI object-initializer pattern
            Check("RepoPackageInfo.File alias writes", alias.Filename == "alias.npkg" && alias.Size == 0);

            CheckThrows("RepositoryIndex rejects wrong format", () => RepositoryIndex.FromJson(Json.Parse("{\"format\":\"npkg-repo/9\"}")));
        }

        private static RepoPackageInfo MakeRepoInfo(string name, string version, string filename)
        {
            RepoPackageInfo info = new RepoPackageInfo();
            info.Name = name;
            info.Version = SemVersion.Parse(version);
            info.Filename = filename;
            info.Size = 1234;
            info.Sha256 = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
            return info;
        }

        // ------------------------------------------------------- 8. Ed25519 RFC

        private static void TestEd25519Rfc8032()
        {
            byte[] seed = Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
            byte[] expectedPublic = Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
            byte[] expectedSignature = Hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155"
                + "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

            byte[] publicKey = Ed25519.PublicKeyFromSeed(seed);
            Check("Ed25519 RFC 8032 TEST 1 public key", BytesEqual(publicKey, expectedPublic));
            byte[] signature = Ed25519.Sign(seed, new byte[0]);
            Check("Ed25519 RFC 8032 TEST 1 signature", BytesEqual(signature, expectedSignature));
            Check("Ed25519 RFC 8032 TEST 1 verify", Ed25519.Verify(expectedPublic, new byte[0], expectedSignature));

            string expectedFingerprint = ToHexLower(SHA256.HashData(expectedPublic));
            Check("NpkgPackage.Fingerprint == SHA-256 of public key",
                NpkgPackage.Fingerprint(expectedPublic) == expectedFingerprint);

            NpkgManifest manifest = new NpkgManifest();
            manifest.Name = "tests.rfc8032";
            manifest.Version = SemVersion.Parse("1.0.0");
            byte[] packageBytes = NpkgPackage.Build(manifest, new KeyValuePair<string, byte[]>[0], seed);
            NpkgPackage package = NpkgPackage.Open(packageBytes);
            Check("NpkgPackage signature interoperates with standard Ed25519", package.VerifySignature(expectedPublic));
            Check("NpkgPackage signer is the RFC key fingerprint", package.Manifest.Signer == expectedFingerprint);
        }

        // --------------------------------------------------------------- helpers

        private static void Check(string name, bool ok)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine("[PASS] " + name);
            }
            else
            {
                _fail++;
                Console.WriteLine("[FAIL] " + name);
            }
        }

        private static void CheckThrows(string name, Action action)
        {
            try
            {
                action();
                Check(name, false);
            }
            catch (FormatException)
            {
                Check(name, true);
            }
            catch (Exception ex)
            {
                Check(name + " (wrong exception: " + ex.GetType().Name + ")", false);
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static byte[] CopyOf(byte[] data)
        {
            byte[] copy = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                copy[i] = data[i];
            return copy;
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }

        private static KeyValuePair<string, byte[]> Kv(string key, byte[] value)
        {
            return new KeyValuePair<string, byte[]>(key, value);
        }

        private static byte[] Hex(string text)
        {
            byte[] bytes = TextConv.HexDecode(text);
            if (bytes == null)
                throw new FormatException("self test: invalid hex literal");
            return bytes;
        }

        private static string ToHexLower(byte[] data)
        {
            return Convert.ToHexString(data).ToLowerInvariant();
        }
    }
}
