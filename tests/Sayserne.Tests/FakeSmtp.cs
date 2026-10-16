using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Sayserne.Tests;

/// <summary>A small in-process SMTP server (EHLO, AUTH LOGIN, MAIL, RCPT, DATA) that keeps what it receives, parsed, so tests can check what really goes on the wire.</summary>
public sealed class FakeSmtp : IAsyncDisposable
{
    public sealed record Received(string From, IReadOnlyList<string> Recipients, string Raw, string? Username, string? Password)
    {
        private readonly Lazy<Mime> _mime = new(() => Mime.Parse(Raw));
        public string Subject => _mime.Value.Subject;
        public string Header(string name) => _mime.Value.Headers.GetValueOrDefault(name.ToLowerInvariant(), "");
        public string Text => _mime.Value.Parts.FirstOrDefault(p => p.Type == "text/plain")?.Body ?? "";
        public string Html => _mime.Value.Parts.FirstOrDefault(p => p.Type == "text/html")?.Body ?? "";
        public string To => string.Join(",", Recipients);
    }

    public sealed record Part(string Type, string Body);
    public sealed record Mime(Dictionary<string, string> Headers, string Subject, List<Part> Parts)
    {
        public static Mime Parse(string raw)
        {
            var (headers, body) = Split(raw);
            var parts = new List<Part>();
            var type = headers.GetValueOrDefault("content-type", "text/plain");
            var boundary = Regex.Match(type, "boundary=\"?([^\";]+)\"?");
            if (boundary.Success)
                foreach (var chunk in body.Split("--" + boundary.Groups[1].Value).Skip(1).Where(c => !c.TrimStart().StartsWith("--") && c.Trim().Length > 0))
                {
                    var (ph, pb) = Split(chunk.TrimStart('\r', '\n'));
                    parts.Add(new Part(Regex.Match(ph.GetValueOrDefault("content-type", "text/plain"), @"^[^;]+").Value.Trim().ToLowerInvariant(), Decode(ph.GetValueOrDefault("content-transfer-encoding", "7bit"), pb)));
                }
            else parts.Add(new Part(Regex.Match(type, @"^[^;]+").Value.Trim().ToLowerInvariant(), Decode(headers.GetValueOrDefault("content-transfer-encoding", "7bit"), body)));
            return new Mime(headers, DecodeWords(headers.GetValueOrDefault("subject", "")), parts);
        }

        private static (Dictionary<string, string>, string) Split(string text)
        {
            var i = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = i < 0 ? text : text[..i];
            var body = i < 0 ? "" : text[(i + 4)..];
            var headers = new Dictionary<string, string>();
            string? last = null;
            foreach (var line in head.Split("\r\n"))
            {
                if ((line.StartsWith(' ') || line.StartsWith('\t')) && last is not null) { headers[last] += " " + line.Trim(); continue; }
                var c = line.IndexOf(':');
                if (c <= 0) continue;
                last = line[..c].Trim().ToLowerInvariant();
                headers[last] = line[(c + 1)..].Trim();
            }
            return (headers, body);
        }

        private static string Decode(string encoding, string body) => encoding.ToLowerInvariant() switch
        {
            "base64" => Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(body, @"\s+", ""))),
            "quoted-printable" => DecodeQuotedPrintable(body),
            _ => body,
        };

        private static string DecodeQuotedPrintable(string body)
        {
            var bytes = new List<byte>();
            var text = body.Replace("=\r\n", "");
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '=' && i + 2 < text.Length + 0 && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2])) { bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16)); i += 2; }
                else bytes.AddRange(Encoding.UTF8.GetBytes(text[i].ToString()));
            return Encoding.UTF8.GetString([.. bytes]);
        }

        public static string DecodeWords(string value) => Regex.Replace(Regex.Replace(value, @"\?=\s+=\?", "?==?"), @"=\?utf-8\?([BbQq])\?(.*?)\?=", m =>
            m.Groups[1].Value.ToUpperInvariant() == "B" ? Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[2].Value)) : DecodeQuotedPrintable(m.Groups[2].Value.Replace('_', ' ')), RegexOptions.IgnoreCase);
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    public ConcurrentQueue<Received> Messages { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    /// <summary>The next N messages are refused with a 550 (to test retries).</summary>
    public int RejectNext;
    public int Connections;

    public static Task<FakeSmtp> StartAsync()
    {
        var s = new FakeSmtp();
        s._listener.Start();
        s._loop = Task.Run(() => s.AcceptAsync());
        return Task.FromResult(s);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        Interlocked.Increment(ref Connections);
        using var _ = client;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        string? from = null, user = null, pass = null;
        var rcpt = new List<string>();
        await writer.WriteLineAsync("220 fake.smtp ready");
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var cmd = line.Split(' ')[0].ToUpperInvariant();
                switch (cmd)
                {
                    case "EHLO" or "HELO": await writer.WriteAsync("250-fake.smtp\r\n250-AUTH LOGIN\r\n250 8BITMIME\r\n"); break;
                    case "AUTH":
                        var arg = line.Length > 11 ? line[11..] : "";
                        if (arg.Length == 0) { await writer.WriteLineAsync("334 VXNlcm5hbWU6"); arg = (await reader.ReadLineAsync())!; }
                        user = Encoding.UTF8.GetString(Convert.FromBase64String(arg));
                        await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                        pass = Encoding.UTF8.GetString(Convert.FromBase64String((await reader.ReadLineAsync())!));
                        await writer.WriteLineAsync("235 authenticated");
                        break;
                    case "MAIL": from = Regex.Match(line, "<(.*)>").Groups[1].Value; rcpt.Clear(); await writer.WriteLineAsync("250 ok"); break;
                    case "RCPT": rcpt.Add(Regex.Match(line, "<(.*)>").Groups[1].Value); await writer.WriteLineAsync("250 ok"); break;
                    case "DATA":
                        await writer.WriteLineAsync("354 go ahead");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } l && l != ".") data.Append(l.StartsWith("..") ? l[1..] : l).Append("\r\n");
                        if (Interlocked.Decrement(ref RejectNext) >= 0) { await writer.WriteLineAsync("550 mailbox unavailable"); break; }
                        Interlocked.Exchange(ref RejectNext, Math.Max(RejectNext, 0));
                        Messages.Enqueue(new Received(from ?? "", [.. rcpt], data.ToString(), user, pass));
                        await writer.WriteLineAsync("250 queued");
                        break;
                    case "RSET" or "NOOP": await writer.WriteLineAsync("250 ok"); break;
                    case "QUIT": await writer.WriteLineAsync("221 bye"); return;
                    default: await writer.WriteLineAsync("502 not implemented"); break;
                }
            }
        }
        catch (IOException) { /* client went away */ }
    }

    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); try { await _loop; } catch (Exception) { } _stop.Dispose(); }
}
