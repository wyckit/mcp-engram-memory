using System.Text.Json;
using McpVectorMemory;

namespace McpVectorMemory.Tests;

public class IndexPersistenceTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"persist_test_{Guid.NewGuid()}.json");
        _tempFiles.Add(path);
        _tempFiles.Add(path + ".tmp");
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_CreatesDirectoryIfNeeded()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"persist_test_dir_{Guid.NewGuid()}");
        var path = Path.Combine(dir, "index.json");
        _tempFiles.Add(path);
        try
        {
            IndexPersistence.Save(path, new[] { new VectorEntry("a", new float[] { 1f }) });
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_EmptyList_WritesEmptyArray()
    {
        var path = TempPath();
        IndexPersistence.Save(path, Array.Empty<VectorEntry>());
        var content = File.ReadAllText(path);
        Assert.Equal("[]", content);
    }

    [Fact]
    public void Save_NoTmpFileLeftBehind()
    {
        var path = TempPath();
        IndexPersistence.Save(path, new[] { new VectorEntry("a", new float[] { 1f }) });
        Assert.False(File.Exists(path + ".tmp"));
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NonExistentFile_ReturnsEmpty()
    {
        var entries = IndexPersistence.Load("/tmp/does_not_exist_" + Guid.NewGuid() + ".json");
        Assert.Empty(entries);
    }

    [Fact]
    public void Load_EmptyArray_ReturnsEmpty()
    {
        var path = TempPath();
        File.WriteAllText(path, "[]");
        var entries = IndexPersistence.Load(path);
        Assert.Empty(entries);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmpty()
    {
        var path = TempPath();
        File.WriteAllText(path, "{{{ not json !!!");
        var entries = IndexPersistence.Load(path);
        Assert.Empty(entries);
    }

    [Fact]
    public void Load_NullJson_ReturnsEmpty()
    {
        var path = TempPath();
        File.WriteAllText(path, "null");
        var entries = IndexPersistence.Load(path);
        Assert.Empty(entries);
    }

    [Fact]
    public void Load_SkipsEntriesWithMissingId()
    {
        var path = TempPath();
        // Entry with empty ID should be skipped, valid entry kept
        File.WriteAllText(path, """[{"id":"","vector":[1]},{"id":"good","vector":[2]}]""");
        var entries = IndexPersistence.Load(path);
        Assert.Single(entries);
        Assert.Equal("good", entries[0].Id);
    }

    [Fact]
    public void Load_SkipsEntriesWithEmptyVector()
    {
        var path = TempPath();
        File.WriteAllText(path, """[{"id":"a","vector":[]},{"id":"b","vector":[1]}]""");
        var entries = IndexPersistence.Load(path);
        Assert.Single(entries);
        Assert.Equal("b", entries[0].Id);
    }

    // ── Round trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var path = TempPath();
        var original = new VectorEntry("test-1", new float[] { 1.5f, -2.3f, 0f, 4.1f },
            "sample text",
            new Dictionary<string, string> { ["source"] = "unit-test", ["priority"] = "high" });

        IndexPersistence.Save(path, new[] { original });
        var loaded = IndexPersistence.Load(path);

        Assert.Single(loaded);
        var entry = loaded[0];
        Assert.Equal("test-1", entry.Id);
        Assert.Equal(original.Vector, entry.Vector);
        Assert.Equal("sample text", entry.Text);
        Assert.Equal("unit-test", entry.Metadata["source"]);
        Assert.Equal("high", entry.Metadata["priority"]);
    }

    [Fact]
    public void RoundTrip_NullTextAndMetadata()
    {
        var path = TempPath();
        var original = new VectorEntry("no-meta", new float[] { 1f, 2f });
        IndexPersistence.Save(path, new[] { original });
        var loaded = IndexPersistence.Load(path);

        Assert.Single(loaded);
        Assert.Null(loaded[0].Text);
        Assert.Empty(loaded[0].Metadata);
    }

    [Fact]
    public void RoundTrip_SpecialCharactersInText()
    {
        var path = TempPath();
        var original = new VectorEntry("special", new float[] { 1f },
            "line1\nline2\ttab \"quoted\" \\backslash unicode: \u00e9\u00f1\u00fc");
        IndexPersistence.Save(path, new[] { original });
        var loaded = IndexPersistence.Load(path);

        Assert.Single(loaded);
        Assert.Equal(original.Text, loaded[0].Text);
    }

    [Fact]
    public void RoundTrip_SpecialCharactersInMetadata()
    {
        var path = TempPath();
        var meta = new Dictionary<string, string>
        {
            ["key with spaces"] = "value with \"quotes\"",
            ["unicode"] = "\u00e9\u00f1\u00fc\u2603"
        };
        var original = new VectorEntry("meta-special", new float[] { 1f }, metadata: meta);
        IndexPersistence.Save(path, new[] { original });
        var loaded = IndexPersistence.Load(path);

        Assert.Single(loaded);
        Assert.Equal("value with \"quotes\"", loaded[0].Metadata["key with spaces"]);
        Assert.Equal("\u00e9\u00f1\u00fc\u2603", loaded[0].Metadata["unicode"]);
    }

    [Fact]
    public void RoundTrip_ManyEntries()
    {
        var path = TempPath();
        var entries = new List<VectorEntry>();
        for (int i = 0; i < 500; i++)
            entries.Add(new VectorEntry($"v{i}", new float[] { i + 1f, i + 2f }));

        IndexPersistence.Save(path, entries);
        var loaded = IndexPersistence.Load(path);
        Assert.Equal(500, loaded.Count);
    }

    [Fact]
    public void RoundTrip_HighDimensionalVector()
    {
        var path = TempPath();
        var rng = new Random(42);
        var vec = new float[1536]; // typical OpenAI embedding size
        for (int i = 0; i < vec.Length; i++)
            vec[i] = (float)(rng.NextDouble() * 2 - 1);
        var original = new VectorEntry("high-dim", vec);

        IndexPersistence.Save(path, new[] { original });
        var loaded = IndexPersistence.Load(path);

        Assert.Single(loaded);
        Assert.Equal(1536, loaded[0].Vector.Length);
        for (int i = 0; i < vec.Length; i++)
            Assert.Equal(vec[i], loaded[0].Vector[i]);
    }

    // ── .tmp recovery ────────────────────────────────────────────────────────

    [Fact]
    public void Load_TmpOnly_RecoversTmp()
    {
        var path = TempPath();
        var tmpPath = path + ".tmp";

        // Write valid data to .tmp only (simulate crash mid-rename)
        IndexPersistence.Save(path, new[] { new VectorEntry("a", new float[] { 1f }) });
        File.Move(path, tmpPath, overwrite: true);

        var loaded = IndexPersistence.Load(path);
        Assert.Single(loaded);
        Assert.Equal("a", loaded[0].Id);

        // .tmp should be promoted to main file
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_CorruptMainValidTmp_UsesTmp()
    {
        var path = TempPath();
        var tmpPath = path + ".tmp";

        // Save valid data then move to .tmp
        IndexPersistence.Save(path, new[] { new VectorEntry("from-tmp", new float[] { 1f }) });
        File.Move(path, tmpPath, overwrite: true);

        // Write corrupt main
        File.WriteAllText(path, "NOT JSON{{{");

        var loaded = IndexPersistence.Load(path);
        Assert.Single(loaded);
        Assert.Equal("from-tmp", loaded[0].Id);
    }

    [Fact]
    public void Load_BothCorrupt_ReturnsEmpty()
    {
        var path = TempPath();
        File.WriteAllText(path, "CORRUPT");
        File.WriteAllText(path + ".tmp", "ALSO CORRUPT");

        var loaded = IndexPersistence.Load(path);
        Assert.Empty(loaded);
    }

    // ── Overwrite behavior ───────────────────────────────────────────────────

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var path = TempPath();
        IndexPersistence.Save(path, new[] { new VectorEntry("old", new float[] { 1f }) });
        IndexPersistence.Save(path, new[] { new VectorEntry("new", new float[] { 2f }) });

        var loaded = IndexPersistence.Load(path);
        Assert.Single(loaded);
        Assert.Equal("new", loaded[0].Id);
    }
}
