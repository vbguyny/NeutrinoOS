// npkg-repo-server - the NeutrinoOS local package repository server.
//
// Serves a directory of .npkg packages over HTTP/1.1 to NeutrinoOS
// devices (http:// only in Phase 8).  On every index request the
// directory is re-scanned and repository.json is regenerated (and signed
// when a private key is configured), so `npkg publish`-ing a new package
// into the directory makes it visible immediately.
//
//   GET /                     directory listing (HTML)
//   GET /repository.json      signed repository index
//   GET /repository.json.sig  Ed25519 signature over the raw JSON bytes
//   GET /repo.pub             signing public key (64 hex + newline)
//   GET /<file>.npkg          package files
//
// The index/signature generation shares the NeutrinoOS.Packaging sources
// with the on-device npkg and the host npkg CLI, so all three agree
// byte-for-byte.
//
// Usage:
//   npkg-repo-server --dir <repo-dir> [--port 8080] [--bind 0.0.0.0]
//                    [--key <private.key>] [--name <repo-name>] [--quiet]
//
// Windows:  scripts\start-repo-server.ps1  (see docs/SDK-CICD.md / SDK-PACKAGING.md)

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NeutrinoOS.Packaging;
using ProtonOS.DDK.Crypto;

namespace NeutrinoOS.Sdk.RepoServer
{
    internal static class Program
    {
        private const string ToolVersion = "1.0.0";

        private static string _dir;
        private static string _keyPath;
        private static byte[] _keySeed;
        private static string _repoName;
        private static bool _quiet;

        private static int Main(string[] args)
        {
            string bind = "0.0.0.0";
            int port = 8080;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    switch (arg)
                    {
                        case "--dir": _dir = Next(args, ref i, arg); break;
                        case "--key": _keyPath = Next(args, ref i, arg); break;
                        case "--name": _repoName = Next(args, ref i, arg); break;
                        case "--bind": bind = Next(args, ref i, arg); break;
                        case "--port": port = int.Parse(Next(args, ref i, arg)); break;
                        case "--quiet": _quiet = true; break;
                        case "--help":
                        case "-h":
                            PrintUsage();
                            return 0;
                        case "--version":
                            Console.WriteLine("npkg-repo-server (NeutrinoOS) " + ToolVersion);
                            return 0;
                        default:
                            throw new Exception("unknown argument: " + arg);
                    }
                }

                if (string.IsNullOrEmpty(_dir))
                    throw new Exception("missing required option --dir");

                _dir = Path.GetFullPath(_dir);
                if (!Directory.Exists(_dir))
                    Directory.CreateDirectory(_dir);

                if (!string.IsNullOrEmpty(_keyPath))
                    _keySeed = ReadHexKeyFile(_keyPath);

                int count = RegenerateIndex();
                string fingerprint = _keySeed != null
                    ? NpkgPackage.Fingerprint(Ed25519.PublicKeyFromSeed(_keySeed))
                    : "none (UNSIGNED index - run 'npkg-host keygen' and pass --key)";

                Console.WriteLine("npkg-repo-server (NeutrinoOS) " + ToolVersion);
                Console.WriteLine("repo dir : " + _dir + " (" + count + " package" + (count == 1 ? "" : "s") + ")");
                Console.WriteLine("signing  : " + fingerprint);
                Console.WriteLine("serving  : http://" + bind + ":" + port + "/ (Ctrl+C to stop)");

                TcpListener listener = new TcpListener(IPAddress.Parse(bind), port);
                listener.Start();

                while (true)
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        try
                        {
                            Handle(client);
                        }
                        catch (Exception ex)
                        {
                            if (!_quiet) Console.WriteLine("[warn] request failed: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("npkg-repo-server: " + ex.Message);
                return 1;
            }
        }

        // ------------------------------------------------------------ index

        /// <summary>Re-scans the directory and rewrites repository.json (+ signature). Returns the package count.</summary>
        private static int RegenerateIndex()
        {
            string[] pkgs = Directory.GetFiles(_dir, "*.npkg");
            Array.Sort(pkgs, StringComparer.Ordinal);

            RepoPackageList infos = new RepoPackageList();
            foreach (string file in pkgs)
            {
                byte[] bytes = File.ReadAllBytes(file);
                NpkgPackage pkg = NpkgPackage.Open(bytes);
                NpkgManifest manifest = pkg.Manifest;

                RepoPackageInfo info = new RepoPackageInfo();
                info.Name = manifest.Name;
                info.Version = manifest.Version;
                info.Architecture = manifest.Architecture;
                info.Description = manifest.Description;
                info.Filename = Path.GetFileName(file);
                info.Size = bytes.Length;
                info.Sha256 = NpkgPackage.Sha256Hex(bytes);
                info.Signer = manifest.Signer != null ? manifest.Signer : "";
                info.Dependencies = manifest.Dependencies;
                if (manifest.Provides != null)
                    info.Provides = manifest.Provides.ToArray();
                infos.Add(info);
            }

            RepositoryIndex index = new RepositoryIndex();
            index.Format = "npkg-repo/1";
            index.Revision = 1;
            index.Generated = "";
            index.Name = _repoName != null ? _repoName : "";
            index.Packages = infos;

            string json = Json.Write(index.ToJson(), true);
            string indexPath = Path.Combine(_dir, "repository.json");
            File.WriteAllText(indexPath, json);

            if (_keySeed != null)
            {
                byte[] pub = Ed25519.PublicKeyFromSeed(_keySeed);
                byte[] sig = Ed25519.Sign(_keySeed, File.ReadAllBytes(indexPath));
                File.WriteAllBytes(indexPath + ".sig", sig);

                string pubPath = Path.Combine(_dir, "repo.pub");
                string pubHex = TextConv.HexEncode(pub) + "\n";
                if (!File.Exists(pubPath) || File.ReadAllText(pubPath) != pubHex)
                    File.WriteAllText(pubPath, pubHex);
            }

            return infos.Count;
        }

        // ------------------------------------------------------------ http

        private static void Handle(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 10000;
            stream.WriteTimeout = 30000;

            string requestLine = ReadLine(stream);
            if (requestLine == null)
                return;

            // Drain headers.
            while (true)
            {
                string header = ReadLine(stream);
                if (header == null || header.Length == 0)
                    break;
            }

            string[] parts = requestLine.Split(' ');
            string method = parts.Length > 0 ? parts[0] : "";
            string path = parts.Length > 1 ? parts[1] : "/";

            int query = path.IndexOf('?');
            if (query >= 0) path = path.Substring(0, query);
            path = Uri.UnescapeDataString(path);

            if (method != "GET" && method != "HEAD")
            {
                WriteResponse(stream, "405 Method Not Allowed", "text/plain", Encoding.UTF8.GetBytes("method not allowed"), method == "HEAD", requestLine);
                return;
            }

            if (path == "/" || path == "/index.html")
            {
                WriteResponse(stream, "200 OK", "text/html; charset=utf-8", BuildDirectoryPage(), method == "HEAD", requestLine);
                return;
            }

            // Index and key endpoints are regenerated on demand.
            if (path == "/repository.json" || path == "/repository.json.sig" || path == "/repo.pub")
            {
                RegenerateIndex();
                ServeFile(stream, path.TrimStart('/'), method, requestLine);
                return;
            }

            ServeFile(stream, path.TrimStart('/'), method, requestLine);
        }

        private static void ServeFile(NetworkStream stream, string relative, string method, string requestLine)
        {
            if (relative.Length == 0 || relative.Contains("..") || relative.Contains(":") || relative.Contains("\\"))
            {
                WriteResponse(stream, "400 Bad Request", "text/plain", Encoding.UTF8.GetBytes("bad path"), method == "HEAD", requestLine);
                return;
            }

            string full = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                WriteResponse(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found: /" + relative), method == "HEAD", requestLine);
                return;
            }

            string contentType = relative.EndsWith(".json") ? "application/json"
                : relative.EndsWith(".sig") || relative.EndsWith(".npkg") ? "application/octet-stream"
                : "text/plain";
            byte[] body = File.ReadAllBytes(full);
            WriteResponse(stream, "200 OK", contentType, body, method == "HEAD", requestLine);
        }

        private static void WriteResponse(NetworkStream stream, string status, string contentType, byte[] body, bool headOnly, string requestLine)
        {
            if (!_quiet && requestLine != null)
                Console.WriteLine(status.Substring(0, 3) + " " + requestLine);

            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] header = Encoding.ASCII.GetBytes(sb.ToString());

            stream.Write(header, 0, header.Length);
            if (!headOnly && body.Length > 0)
                stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static byte[] BuildDirectoryPage()
        {
            string[] pkgs = Directory.GetFiles(_dir, "*.npkg");
            Array.Sort(pkgs, StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><title>NeutrinoOS package repository</title></head><body>");
            sb.Append("<h1>NeutrinoOS package repository</h1>");
            sb.Append("<p><a href=\"/repository.json\">repository.json</a> &middot; ")
              .Append("<a href=\"/repo.pub\">repo.pub</a></p>");
            sb.Append("<ul>");
            foreach (string f in pkgs)
            {
                string name = Path.GetFileName(f);
                sb.Append("<li><a href=\"/").Append(name).Append("\">").Append(name).Append("</a></li>");
            }
            sb.Append("</ul></body></html>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static string ReadLine(NetworkStream stream)
        {
            StringBuilder sb = new StringBuilder(128);
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    return sb.Length > 0 ? sb.ToString() : null;
                if (b == '\n')
                    return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
                if (sb.Length > 8192)
                    return sb.ToString();
            }
        }

        // ------------------------------------------------------------ util

        private static string Next(string[] args, ref int i, string what)
        {
            if (i + 1 >= args.Length)
                throw new Exception("missing value for " + what);
            return args[++i];
        }

        private static byte[] ReadHexKeyFile(string path)
        {
            if (!File.Exists(path))
                throw new Exception("key file not found: " + path);
            string hex = File.ReadAllText(path).Trim();
            byte[] bytes = TextConv.HexDecode(hex);
            if (bytes == null || bytes.Length != 32)
                throw new Exception(path + " must be 64 hex characters (32 bytes)");
            return bytes;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("npkg-repo-server (NeutrinoOS) " + ToolVersion + " - local package repository server");
            Console.WriteLine();
            Console.WriteLine("usage: npkg-repo-server --dir <repo-dir> [--port 8080] [--bind 0.0.0.0]");
            Console.WriteLine("                        [--key <private.key>] [--name <repo-name>] [--quiet]");
        }
    }
}
