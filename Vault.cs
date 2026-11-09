using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cove;

sealed class Vault
{
    const int Iterations = 200_000;
    static readonly byte[] VerifierText = "cove-ok"u8.ToArray();
    readonly string _path;
    byte[]? _key;
    int _misses;

    public Vault(string root)
    {
        _path = Path.Combine(root, "vault.json");
    }

    public bool Exists => File.Exists(_path);
    public bool IsUnlocked => _key is not null;
    public bool HasWindowsUnlock => MacKeychain.Exists("Cove", "vault-key");
    public List<VaultEntry> Entries { get; } = [];

    public void Create(string password)
    {
        _key = Derive(password, RandomNumberGenerator.GetBytes(16), out var salt);
        Entries.Clear();
        Write(salt, Encrypt(_key, VerifierText));
    }

    public void CreateForWindows()
    {
        _key = RandomNumberGenerator.GetBytes(32);
        Entries.Clear();
        Write(RandomNumberGenerator.GetBytes(16), Encrypt(_key, VerifierText));
        EnableWindowsUnlock();
    }

    public void EnableWindowsUnlock()
    {
        if (_key is null) return;
        MacKeychain.Save("Cove", "vault-key", _key);
    }

    public bool Unlock(string password)
    {
        if (_misses >= 5 || !File.Exists(_path)) return false;
        var file = Read();
        var key = Derive(password, Convert.FromBase64String(file.Salt), out _);
        if (!AcceptKey(file, key))
        {
            _misses++;
            return false;
        }
        _misses = 0;
        return true;
    }

    public bool UnlockWithWindows()
    {
        if (!HasWindowsUnlock || !File.Exists(_path)) return false;
        try
        {
            var key = MacKeychain.Load("Cove", "vault-key");
            return key is not null && AcceptKey(Read(), key);
        }
        catch
        {
            return false;
        }
    }

    bool AcceptKey(FileModel file, byte[] key)
    {
        try
        {
            var plain = Decrypt(key, Convert.FromBase64String(file.Verifier));
            if (!plain.AsSpan().SequenceEqual(VerifierText)) throw new CryptographicException();
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            return false;
        }

        _key = key;
        Entries.Clear();
        foreach (var entry in file.Entries)
        {
            Entries.Add(new VaultEntry
            {
                Id = entry.Id,
                Host = entry.Host,
                Username = Encoding.UTF8.GetString(Decrypt(key, Convert.FromBase64String(entry.Username))),
                Password = Encoding.UTF8.GetString(Decrypt(key, Convert.FromBase64String(entry.Secret)))
            });
        }
        return true;
    }

    public void Lock()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        Entries.Clear();
    }

    public void Add(string host, string username, string password)
    {
        if (_key is null) throw new InvalidOperationException("Vault is locked.");
        var existing = Entries.FirstOrDefault(e =>
            e.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
            e.Username.Equals(username, StringComparison.Ordinal));
        if (existing is null)
        {
            existing = new VaultEntry { Host = host, Username = username, Password = password };
            Entries.Add(existing);
        }
        else
        {
            existing.Password = password;
        }
        Persist();
    }

    public void Delete(string id)
    {
        Entries.RemoveAll(e => e.Id == id);
        Persist();
    }

    void Persist()
    {
        if (_key is null) return;
        var file = Read();
        var salt = Convert.FromBase64String(file.Salt);
        Write(salt, Convert.FromBase64String(file.Verifier));
    }

    void Write(byte[] salt, byte[] verifier)
    {
        if (_key is null) return;
        var model = new FileModel
        {
            Salt = Convert.ToBase64String(salt),
            Verifier = Convert.ToBase64String(verifier),
            Entries = Entries.Select(e => new EntryModel
            {
                Id = e.Id,
                Host = e.Host,
                Username = Convert.ToBase64String(Encrypt(_key, Encoding.UTF8.GetBytes(e.Username))),
                Secret = Convert.ToBase64String(Encrypt(_key, Encoding.UTF8.GetBytes(e.Password)))
            }).ToList()
        };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model));
        File.Move(tmp, _path, overwrite: true);
    }

    FileModel Read() => JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path)) ?? new FileModel();

    static byte[] Derive(string password, byte[] salt, out byte[] saltOut)
    {
        saltOut = salt;
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
    }

    static byte[] Encrypt(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var blob = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob.AsSpan(nonce.Length));
        cipher.CopyTo(blob.AsSpan(nonce.Length + tag.Length));
        return blob;
    }

    static byte[] Decrypt(byte[] key, byte[] blob)
    {
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var cipher = blob.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    sealed class FileModel
    {
        public string Salt { get; set; } = "";
        public string Verifier { get; set; } = "";
        public List<EntryModel> Entries { get; set; } = [];
    }

    sealed class EntryModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("n");
        public string Host { get; set; } = "";
        public string Username { get; set; } = "";
        public string Secret { get; set; } = "";
    }
}

