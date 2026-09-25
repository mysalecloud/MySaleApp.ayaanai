using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.Persistence;

namespace MySale.AI.Infrastructure.Media;

public sealed class AttachmentStorageOptions
{
    public const string Section = "Attachments";
    /// <summary>Folder outside the web root. Files are named by id only; nothing is ever served as static content.</summary>
    public string RootPath { get; set; } = "App_Data/attachments";
    public ClamAvOptions ClamAv { get; set; } = new();
}

public sealed class ClamAvOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3310;
    /// <summary>When the scanner is enabled but unreachable: reject (true) or accept with a warning (false).</summary>
    public bool FailClosed { get; set; } = true;
}

/// <summary>Stores uploads under RootPath/{company-hash}/{id}.bin. Keys are server-generated and validated on every access.</summary>
public sealed class FileSystemAttachmentStore : IAttachmentStore
{
    private readonly string _root;

    public FileSystemAttachmentStore(IOptions<AttachmentStorageOptions> options)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(string companyId, string attachmentId, Stream content, CancellationToken ct)
    {
        if (!IsSafeSegment(attachmentId)) throw new InvalidOperationException("Invalid attachment id.");
        var folder = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(companyId)))[..16].ToLowerInvariant();
        var key = $"{folder}/{attachmentId}.bin";
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct)
    {
        var path = Resolve(storageKey);
        Stream? stream = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        var path = Resolve(storageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>Rejects anything that could escape the storage root.</summary>
    private string Resolve(string key)
    {
        var parts = (key ?? string.Empty).Split('/');
        if (parts.Length != 2 || !IsSafeSegment(parts[0]) || !IsSafeSegment(Path.GetFileNameWithoutExtension(parts[1])) || !parts[1].EndsWith(".bin", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid storage key.");
        var full = Path.GetFullPath(Path.Combine(_root, parts[0], parts[1]));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid storage key.");
        return full;
    }

    private static bool IsSafeSegment(string s) => s.Length is > 0 and <= 64 && s.All(char.IsAsciiLetterOrDigit);
}

public sealed class NoOpFileScanner : IFileScanner
{
    public Task<ScanResult> ScanAsync(Stream content, CancellationToken ct) => Task.FromResult(new ScanResult { Clean = true, Scanner = "none" });
}

/// <summary>ClamAV clamd INSTREAM scanner (enable with Attachments:ClamAv:Enabled).</summary>
public sealed class ClamAvFileScanner : IFileScanner
{
    private readonly ClamAvOptions _options;
    private readonly ILogger<ClamAvFileScanner> _logger;

    public ClamAvFileScanner(IOptions<AttachmentStorageOptions> options, ILogger<ClamAvFileScanner> logger)
    {
        _options = options.Value.ClamAv;
        _logger = logger;
    }

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(_options.Host, _options.Port, timeout.Token);
            await using var net = client.GetStream();
            await net.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), timeout.Token);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await content.ReadAsync(buffer, timeout.Token)) > 0)
            {
                var size = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(read));
                await net.WriteAsync(size, timeout.Token);
                await net.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            await net.WriteAsync(new byte[4], timeout.Token);
            using var reader = new StreamReader(net, Encoding.ASCII);
            var reply = (await reader.ReadToEndAsync(timeout.Token)).TrimEnd('\0', '\n');
            var clean = reply.EndsWith("OK", StringComparison.Ordinal);
            return new ScanResult { Clean = clean, Threat = clean ? null : reply, Scanner = "clamav" };
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "ClamAV unavailable");
            return _options.FailClosed
                ? new ScanResult { Clean = false, Threat = "scanner unavailable", Scanner = "clamav" }
                : new ScanResult { Clean = true, Scanner = "clamav-unavailable" };
        }
        finally
        {
            content.Position = 0;
        }
    }
}

public sealed class AttachmentRepository : IAttachmentRepository
{
    private readonly SystemDbContext _db;
    public AttachmentRepository(SystemDbContext db) => _db = db;

    public Task InsertAsync(Attachment attachment, CancellationToken ct) => _db.Attachments.InsertOneAsync(attachment, cancellationToken: ct);

    public Task UpdateAsync(Attachment attachment, CancellationToken ct)
        => _db.Attachments.ReplaceOneAsync(a => a.Id == attachment.Id, attachment, cancellationToken: ct);

    public async Task<Attachment?> GetAsync(string id, CancellationToken ct)
        => SystemDbContext.IsObjectId(id) ? await _db.Attachments.Find(a => a.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task<List<Attachment>> GetManyAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var valid = ids.Where(SystemDbContext.IsObjectId).Distinct().ToList();
        return valid.Count == 0
            ? Task.FromResult(new List<Attachment>())
            : _db.Attachments.Find(Builders<Attachment>.Filter.In(a => a.Id, valid)).ToListAsync(ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct) => _db.Attachments.DeleteOneAsync(a => a.Id == id, ct);

    public Task<List<Attachment>> ListExpiredAsync(DateTime now, int limit, CancellationToken ct)
        => _db.Attachments.Find(a => a.ExpiresAt < now).Limit(limit).ToListAsync(ct);
}

/// <summary>Deletes expired attachments (files + records) every hour.</summary>
public sealed class AttachmentCleanupService : BackgroundService
{
    private readonly IAttachmentRepository _repository;
    private readonly IAttachmentStore _store;
    private readonly ILogger<AttachmentCleanupService> _logger;

    public AttachmentCleanupService(IAttachmentRepository repository, IAttachmentStore store, ILogger<AttachmentCleanupService> logger)
    {
        _repository = repository;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await _repository.ListExpiredAsync(DateTime.UtcNow, 500, stoppingToken);
                foreach (var a in expired)
                {
                    if (!string.IsNullOrEmpty(a.StorageKey)) await _store.DeleteAsync(a.StorageKey, stoppingToken);
                    await _repository.DeleteAsync(a.Id, stoppingToken);
                }
                if (expired.Count > 0) _logger.LogInformation("Removed {Count} expired attachments", expired.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Attachment cleanup failed");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
