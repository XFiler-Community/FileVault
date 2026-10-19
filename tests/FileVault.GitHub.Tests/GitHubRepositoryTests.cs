using System.Text;
using FileVault.Core;

namespace FileVault.GitHub.Tests;

[TestFixture]
public class GitHubRepositoryTests
{
    private FakeGitHub _github = null!;
    private GitHubRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _github = new FakeGitHub();
        _repository = _github.CreateRepository();
    }

    [TearDown]
    public void TearDown() => _github.Dispose();

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<string> ReadText(IFileItem item)
    {
        var (stream, _) = await item.OpenReadAsync();
        await using (stream)
            return await new StreamReader(stream).ReadToEndAsync();
    }

    private async Task<IReadOnlyList<IFileProviderItem>> List(string path = "") =>
        await _repository.CreateProvider(path).GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

    // ── Reading ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Listing_FoldersFirst_NestedPaths_FullNames()
    {
        _github.Commit(new Dictionary<string, byte[]?>
        {
            ["CLAUDE.md"] = Text("# Project"),
            ["applications/residency.md"] = Text("draft")
        });

        var root = await List();
        var applications = (IFolderItem)root[0];
        var nested = await applications.CreateProvider().GetItemsAsync(FileProviderFilter.Default).ToListAsync();

        Assert.That(root.Select(i => i.Name), Is.EqualTo(new[] { "applications", "CLAUDE.md" }));
        Assert.That(nested.Single().FullName, Is.EqualTo("github:anna/art-life@main:/applications/residency.md"));
        Assert.That(await ReadText((IFileItem)nested.Single()), Is.EqualTo("draft"));
    }

    [Test]
    public async Task MissingFolder_Throws_ButContainsNothing()
    {
        var provider = _repository.CreateProvider("nowhere");

        Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
            await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync());
        Assert.That(await provider.ContainsFileAsync("a.txt"), Is.False);
    }

    [Test]
    public async Task LfsFile_ShowsObjectSize_ReadsObject_TokenNeverGoesToStorage()
    {
        var picture = new byte[5000];
        new Random(1).NextBytes(picture);
        _github.Commit(new Dictionary<string, byte[]?>
        {
            [".gitattributes"] = Text("*.psd filter=lfs diff=lfs merge=lfs -text\n"),
            ["art/cover.psd"] = _github.AddLfsObject(picture)
        });

        var file = (GitHubFileItem)(await List("art")).Single();
        var (stream, length) = await file.OpenReadAsync();
        byte[] content;
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            content = buffer.ToArray();
        }

        Assert.That(file.IsLfs, Is.True);
        Assert.That(file.Size, Is.EqualTo(5000));
        Assert.That(length, Is.EqualTo(5000));
        Assert.That(content, Is.EqualTo(picture));

        var batch = _github.Requests.Single(r => r.Request.Contains("/info/lfs/objects/batch"));
        var storage = _github.Requests.Single(r => r.Request.Contains("lfs.example"));
        Assert.That(batch.Authorization, Is.EqualTo("Basic"));
        Assert.That(storage.Authorization, Is.Null);
    }

    [Test]
    public async Task OpenReadAt_ReadsAnOlderVersion()
    {
        var first = _github.Commit("notes.md", "one");
        _github.Commit("notes.md", "two");

        await using var stream = await _repository.OpenReadAtAsync(first, "notes.md");

        Assert.That(await new StreamReader(stream!).ReadToEndAsync(), Is.EqualTo("one"));
        Assert.That(await _repository.OpenReadAtAsync(first, "missing.md"), Is.Null);
    }

    private void SeedNestedProject() => _github.Commit(new Dictionary<string, byte[]?>
    {
        ["CLAUDE.md"] = Text("# Project"),
        [".claude/skills/open-calls/SKILL.md"] = Text("skill"),
        [".claude/skills/open-calls/refs/list.md"] = Text("refs"),
        [".claude/agents/editor.md"] = Text("agent"),
        ["applications/2026/residency/CLAUDE.md"] = Text("nested"),
        ["applications/2026/residency/draft.md"] = Text("draft")
    });

    [Test]
    public async Task ListRecursive_WholeBranch_OneTreeRequest_FoldersBeforeContent()
    {
        SeedNestedProject();
        var head = await _repository.GetHeadAsync();
        _github.Requests.Clear();

        var entries = await _repository.ListRecursiveAsync("", head);

        Assert.That(_github.TreeRequests, Is.EqualTo(1));
        Assert.That(entries.Where(e => e.Kind == GitHubEntryKind.File).Select(e => e.Path), Is.EquivalentTo(new[]
        {
            "CLAUDE.md", ".claude/skills/open-calls/SKILL.md", ".claude/skills/open-calls/refs/list.md",
            ".claude/agents/editor.md", "applications/2026/residency/CLAUDE.md", "applications/2026/residency/draft.md"
        }));
        Assert.That(entries.Where(e => e.Kind == GitHubEntryKind.Folder).Select(e => e.Path), Is.EquivalentTo(new[]
        {
            ".claude", ".claude/skills", ".claude/skills/open-calls", ".claude/skills/open-calls/refs",
            ".claude/agents", "applications", "applications/2026", "applications/2026/residency"
        }));

        var paths = entries.Select(e => e.Path).ToList();
        foreach (var entry in entries.Where(e => e.Path.Contains('/')))
            Assert.That(paths.IndexOf(entry.Path[..entry.Path.LastIndexOf('/')]), Is.LessThan(paths.IndexOf(entry.Path)));

        var draft = entries.Single(e => e.Path == "applications/2026/residency/draft.md");
        Assert.That(draft.Size, Is.EqualTo(5));
        Assert.That(draft.Sha, Is.EqualTo(GitHubRepository.BlobSha(Text("draft"))));
    }

    [Test]
    public async Task ListRecursive_FillsTheCache_ListingBelowNeedsNoTreeRequest()
    {
        SeedNestedProject();
        var head = await _repository.GetHeadAsync();
        await _repository.ListRecursiveAsync("", head);
        _github.Requests.Clear();

        var skills = await _repository.ListAsync(".claude/skills/open-calls", head);
        var again = await _repository.ListRecursiveAsync("applications", head);
        var file = await _repository.FindAsync("applications/2026/residency/draft.md", head);

        Assert.That(_github.TreeRequests, Is.Zero);
        Assert.That(skills.Select(e => e.Name), Is.EquivalentTo(new[] { "SKILL.md", "refs" }));
        Assert.That(again.Select(e => e.Path), Does.Contain("applications/2026/residency/CLAUDE.md"));
        Assert.That(file?.Size, Is.EqualTo(5));
    }

    [Test]
    public async Task ListRecursive_Subfolder_PathsFromTheRoot_MissingFolderThrows()
    {
        SeedNestedProject();

        var entries = await _repository.ListRecursiveAsync(".claude/skills");

        Assert.That(entries.Select(e => e.Path), Is.EquivalentTo(new[]
        {
            ".claude/skills/open-calls", ".claude/skills/open-calls/SKILL.md", ".claude/skills/open-calls/refs",
            ".claude/skills/open-calls/refs/list.md"
        }));
        Assert.ThrowsAsync<DirectoryNotFoundException>(() => _repository.ListRecursiveAsync("nowhere"));
        Assert.ThrowsAsync<DirectoryNotFoundException>(() => _repository.ListRecursiveAsync("CLAUDE.md"));
    }

    [Test]
    public async Task ListRecursive_Truncated_WalksFolderByFolder_SameEntries()
    {
        SeedNestedProject();
        var expected = (await _github.CreateRepository().ListRecursiveAsync("")).Select(e => e.Path).ToList();
        _github.RecursiveTreeLimit = 4;
        _github.Requests.Clear();

        var entries = await _repository.ListRecursiveAsync("");

        Assert.That(entries.Select(e => e.Path), Is.EqualTo(expected));
        Assert.That(_github.Requests.Count(r => r.Request.Contains("recursive=1")), Is.GreaterThan(1));
    }

    [Test]
    public async Task ListRecursive_LfsFile_ObjectSize_OrPointerSizeWhenNotResolved()
    {
        var picture = new byte[5000];
        var pointer = _github.AddLfsObject(picture);
        _github.Commit(new Dictionary<string, byte[]?>
        {
            [".gitattributes"] = Text("*.psd filter=lfs diff=lfs merge=lfs -text\n"),
            ["art/covers/cover.psd"] = pointer
        });

        var unresolved = (await _repository.ListRecursiveAsync("", resolveLfsSizes: false))
            .Single(e => e.Name == "cover.psd");
        var resolved = (await _repository.ListRecursiveAsync("")).Single(e => e.Name == "cover.psd");

        Assert.That(unresolved.IsLfs, Is.True);
        Assert.That(unresolved.Size, Is.EqualTo(pointer.Length));
        Assert.That(resolved.IsLfs, Is.True);
        Assert.That(resolved.Size, Is.EqualTo(5000));
    }

    // ── Writing ─────────────────────────────────────────────────────────────

    [Test]
    public async Task WriteFile_WhileUnchanged_CommitsWithMessage()
    {
        _github.Commit("CLAUDE.md", "# Project");
        var opened = (GitHubFileItem)(await List()).Single();

        var result = await _repository.WriteFileAsync("CLAUDE.md", Text("# Project\nmine"), opened.BlobSha,
            "Правка CLAUDE.md с iPad");

        Assert.That(result.Status, Is.EqualTo(GitHubWriteStatus.Written));
        Assert.That(result.BlobSha, Is.EqualTo(GitHubRepository.BlobSha(Text("# Project\nmine"))));
        Assert.That(_github.Read("CLAUDE.md"), Is.EqualTo(Text("# Project\nmine")));
        Assert.That(_github.Message(_github.Head()), Is.EqualTo("Правка CLAUDE.md с iPad"));
    }

    [Test]
    public async Task WriteFile_AgentChangedItMeanwhile_WritesNothing()
    {
        _github.Commit("CLAUDE.md", "# Project");
        var opened = (GitHubFileItem)(await List()).Single();
        _github.Commit("CLAUDE.md", "# Project\nagent", author: "Claude");
        var commits = _github.CommitCount;

        var result = await _repository.WriteFileAsync("CLAUDE.md", Text("# Project\nmine"), opened.BlobSha, "edit");

        Assert.That(result.Status, Is.EqualTo(GitHubWriteStatus.ChangedMeanwhile));
        Assert.That(result.BlobSha, Is.EqualTo(GitHubRepository.BlobSha(Text("# Project\nagent"))));
        Assert.That(_github.CommitCount, Is.EqualTo(commits));
    }

    [Test]
    public async Task WriteFile_NewFileThatAppearedMeanwhile_WritesNothing()
    {
        _github.Commit("notes.md", "agent's");

        var result = await _repository.WriteFileAsync("notes.md", Text("mine"), expectedBlobSha: null, "add");

        Assert.That(result.Status, Is.EqualTo(GitHubWriteStatus.ChangedMeanwhile));
    }

    [Test]
    public async Task Writes_ToLfsPaths_AreRefused()
    {
        _github.Commit(".gitattributes", "portfolio/** filter=lfs diff=lfs merge=lfs -text\n");

        var write = await _repository.WriteFileAsync("portfolio/a.png", [1, 2], null, "add");
        var copy = await _repository.CreateProvider().CopyFileInAsync(new MemoryFile("a.png", [1, 2]),
            _repository.FullName("portfolio/a.png"), new Progress<double>());

        Assert.That(write.Status, Is.EqualTo(GitHubWriteStatus.StoredInLfs));
        Assert.That(copy.Exception, Is.TypeOf<NotSupportedException>());
        Assert.That(_github.Read("portfolio/a.png"), Is.Null);
    }

    [Test]
    public async Task BranchMovedDuringCommit_CommitsAgainOnTop_KeepsBoth()
    {
        _github.Commit("deadlines.csv", "a");
        _github.BeforeRefUpdate = () => _github.Commit("agent.md", "pushed meanwhile", author: "Claude");

        var result = await _repository.CreateProvider().CreateEmptyTextFileAsync("notes.md");

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_github.Read("agent.md"), Is.Not.Null, "the other commit survives");
        Assert.That(_github.Read("notes.md"), Is.Not.Null);
        Assert.That(_github.Requests.Count(r => r.Request.StartsWith("PATCH")), Is.EqualTo(2), "refused once, then retried");
    }

    [Test]
    public async Task FolderOperations_AreSingleCommits()
    {
        _github.Commit(new Dictionary<string, byte[]?>
        {
            ["drafts/a.md"] = Text("a"),
            ["drafts/old/b.md"] = Text("b"),
            ["keep.md"] = Text("k")
        });
        var provider = _repository.CreateProvider();
        var drafts = (await List()).OfType<IFolderItem>().Single();
        var before = _github.CommitCount;

        var rename = await provider.RenameAsync(drafts, "applications");
        var renamed = (await List()).OfType<IFolderItem>().Single();
        var copy = await provider.CopyFolderInAsync(renamed, _repository.FullName("backup"), new Progress<double>());
        var delete = await provider.DeleteAsync([renamed], toRecycleBin: true);

        Assert.That(rename.IsSuccess && copy.IsSuccess && delete.IsSuccess, Is.True);
        Assert.That(_github.CommitCount - before, Is.EqualTo(3));
        Assert.That(_github.FolderExists("drafts"), Is.False);
        Assert.That(_github.FolderExists("applications"), Is.False);
        Assert.That(_github.Read("backup/old/b.md"), Is.EqualTo(Text("b")));
        Assert.That(_github.Read("keep.md"), Is.Not.Null);
    }

    [Test]
    public async Task FolderIntoItself_IsRefused()
    {
        _github.Commit("drafts/a.md", "a");
        var drafts = (await List()).OfType<IFolderItem>().Single();

        var move = await _repository.CreateProvider().TryMoveFolderAsync(drafts, _repository.FullName("drafts/inner"),
            new Progress<double>());

        Assert.That(move.IsSuccess, Is.False);
        Assert.That(_github.Read("drafts/a.md"), Is.Not.Null);
    }

    [Test]
    public async Task ItemsOfAnotherRepository_AreNotTouchedHere()
    {
        _github.Commit("a.md", "a");
        var other = _github.CreateRepository();
        var foreign = (await other.CreateProvider().GetItemsAsync(FileProviderFilter.Default).ToListAsync()).Single();

        var delete = await _repository.CreateProvider().DeleteAsync([foreign], toRecycleBin: false);
        var elsewhere = await _repository.CreateProvider().CopyFileInAsync(new MemoryFile("x.md", [1]),
            "github:anna/other@main:/x.md", new Progress<double>());

        Assert.That(delete.IsSuccess, Is.False);
        Assert.That(elsewhere.IsSuccess, Is.False);
        Assert.That(_github.Read("a.md"), Is.Not.Null);
    }

    [Test]
    public async Task BadNames_AreRefused()
    {
        var provider = _repository.CreateProvider();

        foreach (var name in new[] { "..", ".git", "a/b", "a\\b", "" })
            Assert.That((await provider.CreateFolderAsync(name)).IsSuccess, Is.False, name);
    }

    // ── Several files at once ───────────────────────────────────────────────

    [Test]
    public async Task CommitAsync_WritesRestoresAndDeletes_AsOneCommit()
    {
        var first = _github.Commit(new Dictionary<string, byte[]?>
        {
            ["notes.md"] = Text("one"),
            ["old.md"] = Text("old"),
            ["gone.md"] = Text("gone")
        });
        _github.Commit(new Dictionary<string, byte[]?> { ["notes.md"] = Text("two"), ["old.md"] = Text("changed") });
        var earlier = (await _repository.FindAsync("old.md", await _repository.GetSnapshotAsync(first)))!;
        var head = await _repository.GetHeadAsync();
        var before = _github.CommitCount;

        var result = await _repository.CommitAsync([
            GitHubFileEdit.Write("notes.md", Text("three"), (await _repository.FindAsync("notes.md", head))!.Sha),
            GitHubFileEdit.Write("new/added.md", Text("new"), null),
            GitHubFileEdit.Restore("old.md", earlier, (await _repository.FindAsync("old.md", head))!.Sha),
            GitHubFileEdit.Delete("gone.md", (await _repository.FindAsync("gone.md", head))!.Sha)
        ], "Several files");

        Assert.That(result.Status, Is.EqualTo(GitHubCommitStatus.Committed));
        Assert.That(result.CommitSha, Is.EqualTo(_github.Head()));
        Assert.That(_github.CommitCount - before, Is.EqualTo(1));
        Assert.That(_github.Read("notes.md"), Is.EqualTo(Text("three")));
        Assert.That(_github.Read("new/added.md"), Is.EqualTo(Text("new")));
        Assert.That(_github.Read("old.md"), Is.EqualTo(Text("old")));
        Assert.That(_github.Read("gone.md"), Is.Null);
    }

    [Test]
    public async Task CommitAsync_FileNotAsExpected_WritesNothing_ListsIt()
    {
        _github.Commit(new Dictionary<string, byte[]?> { ["a.md"] = Text("a"), ["b.md"] = Text("b") });
        var head = await _repository.GetHeadAsync();
        var a = (await _repository.FindAsync("a.md", head))!.Sha;
        _github.Commit("b.md", "changed by the agent", author: "Claude");
        var before = _github.Head();

        var result = await _repository.CommitAsync([
            GitHubFileEdit.Write("a.md", Text("mine"), a),
            GitHubFileEdit.Write("b.md", Text("mine"), (await _repository.FindAsync("b.md", head))!.Sha),
            GitHubFileEdit.Write("c.md", Text("mine"), null)
        ], "Mine");

        Assert.That(result.Status, Is.EqualTo(GitHubCommitStatus.ChangedMeanwhile));
        Assert.That(result.Paths, Is.EqualTo(new[] { "b.md" }));
        Assert.That(_github.Head(), Is.EqualTo(before));
    }

    [Test]
    public async Task CommitAsync_ContentForLfsPath_IsRefused()
    {
        _github.Commit(".gitattributes", "*.psd filter=lfs diff=lfs merge=lfs -text\n");

        var result = await _repository.CommitAsync([GitHubFileEdit.Write("cover.psd", [1, 2], null)], "Picture");

        Assert.That(result.Status, Is.EqualTo(GitHubCommitStatus.StoredInLfs));
        Assert.That(result.Paths, Is.EqualTo(new[] { "cover.psd" }));
    }

    [Test]
    public async Task Diff_ReadsOnlyChangedFolders_FolderAddedShowsItsFiles()
    {
        var first = _github.Commit(new Dictionary<string, byte[]?>
        {
            ["keep/big/a.md"] = Text("a"),
            ["notes.md"] = Text("one"),
            ["gone.md"] = Text("gone")
        });
        var second = _github.Commit(new Dictionary<string, byte[]?>
        {
            ["notes.md"] = Text("two"),
            ["gone.md"] = null,
            ["applications/2026/draft.md"] = Text("draft")
        });

        var from = await _repository.GetSnapshotAsync(first);
        var to = await _repository.GetSnapshotAsync(second);
        var keep = (await _repository.FindAsync("keep/big", to))!.Sha;
        _github.Requests.Clear();

        var changes = await _repository.DiffAsync(from, to);

        Assert.That(changes.Select(c => (c.Path, c.Before is not null, c.After is not null)), Is.EquivalentTo(new[]
        {
            ("applications/2026/draft.md", false, true),
            ("gone.md", true, false),
            ("notes.md", true, true)
        }));
        Assert.That(_github.Requests.Any(r => r.Request.Contains(keep)), Is.False, "an unchanged folder is not read");
    }

    [Test]
    public async Task Revert_UndoesEveryFileOfACommit_InOneNewCommit()
    {
        _github.Commit(new Dictionary<string, byte[]?>
        {
            ["deadlines.csv"] = Text("a"),
            ["drafts/old.md"] = Text("old"),
            ["notes.md"] = Text("notes")
        }, "Start");
        var agent = _github.Commit(new Dictionary<string, byte[]?>
        {
            ["deadlines.csv"] = Text("a\nb"),
            ["drafts/old.md"] = null,
            ["applications/residency.md"] = Text("draft")
        }, "Found two open calls\n\nDetails", author: "Claude");
        _github.Commit("other.md", "later, elsewhere");
        var before = _github.CommitCount;

        var result = await _repository.RevertAsync(agent);

        Assert.That(result.Status, Is.EqualTo(GitHubCommitStatus.Committed));
        Assert.That(_github.CommitCount - before, Is.EqualTo(1));
        Assert.That(_github.Message(result.CommitSha!),
            Is.EqualTo($"Revert \"Found two open calls\"\n\nThis reverts commit {agent}."));
        Assert.That(_github.Read("deadlines.csv"), Is.EqualTo(Text("a")));
        Assert.That(_github.Read("drafts/old.md"), Is.EqualTo(Text("old")));
        Assert.That(_github.FolderExists("applications"), Is.False);
        Assert.That(_github.Read("other.md"), Is.Not.Null, "later work stays");
        Assert.That(_github.Read("notes.md"), Is.EqualTo(Text("notes")));
        Assert.That(result.Paths, Is.EquivalentTo(new[] { "deadlines.csv", "drafts/old.md", "applications/residency.md" }));

        Assert.That((await _repository.RevertAsync(agent)).Status, Is.EqualTo(GitHubCommitStatus.NothingToCommit),
            "reverting again finds everything already back");
    }

    [Test]
    public async Task Revert_FileChangedLater_WritesNothing_ListsIt()
    {
        _github.Commit(new Dictionary<string, byte[]?> { ["a.md"] = Text("a"), ["b.md"] = Text("b") });
        var agent = _github.Commit(new Dictionary<string, byte[]?> { ["a.md"] = Text("a2"), ["b.md"] = Text("b2") },
            "Agent", author: "Claude");
        _github.Commit("b.md", "b3 by hand");
        var head = _github.Head();

        var result = await _repository.RevertAsync(agent);

        Assert.That(result.Status, Is.EqualTo(GitHubCommitStatus.ChangedMeanwhile));
        Assert.That(result.Paths, Is.EqualTo(new[] { "b.md" }));
        Assert.That(_github.Head(), Is.EqualTo(head));
    }

    [Test]
    public async Task Revert_PushLandingInBetween_OnAFileOfIt_WritesNothing()
    {
        _github.Commit("a.md", "a");
        var agent = _github.Commit("a.md", "a2", "Agent", author: "Claude");
        _github.BeforeRefUpdate = () => _github.Commit("a.md", "a3 pushed meanwhile", author: "Claude");

        var result = await _repository.RevertAsync(agent);

        Assert.That(result.Status, Is.EqualTo(GitHubCommitStatus.ChangedMeanwhile));
        Assert.That(_github.Read("a.md"), Is.EqualTo(Text("a3 pushed meanwhile")));
    }

    [Test]
    public async Task Revert_LfsFile_ComesBackAsItsPointer()
    {
        var picture = new byte[3000];
        new Random(2).NextBytes(picture);
        _github.Commit(new Dictionary<string, byte[]?>
        {
            [".gitattributes"] = Text("*.psd filter=lfs diff=lfs merge=lfs -text\n"),
            ["cover.psd"] = _github.AddLfsObject(picture)
        });
        var agent = _github.Commit(new Dictionary<string, byte[]?> { ["cover.psd"] = _github.AddLfsObject([1, 2, 3]) },
            "New cover", author: "Claude");

        var result = await _repository.RevertAsync(agent);
        var file = (await _repository.FindAsync("cover.psd"))!;
        await using var stream = await _repository.OpenReadAsync(file);
        using var content = new MemoryStream();
        await stream.CopyToAsync(content);

        Assert.That(result.Status, Is.EqualTo(GitHubCommitStatus.Committed));
        Assert.That(content.ToArray(), Is.EqualTo(picture));
    }

    // ── History ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Commits_ByPath_WithAuthors_AndFilesOfOne()
    {
        _github.Commit("deadlines.csv", "a", "Start");
        _github.Commit(new Dictionary<string, byte[]?>
        {
            ["deadlines.csv"] = Text("a\nb"),
            ["applications/c.md"] = Text("c")
        }, "Found two open calls", author: "Claude", email: "noreply@anthropic.com", login: null);
        _github.Commit("notes.md", "n", "Notes");

        var touching = await _repository.GetCommitsAsync("deadlines.csv");
        var details = await _repository.GetCommitAsync(touching[0].Sha);

        Assert.That(touching.Select(c => c.Message), Is.EqualTo(new[] { "Found two open calls", "Start" }));
        Assert.That(touching[0].AuthorEmail, Is.EqualTo("noreply@anthropic.com"));
        Assert.That(touching[0].AuthorLogin, Is.Null);
        Assert.That(details!.Files!.Select(f => (f.Path, f.Status)), Is.EqualTo(new[]
        {
            ("applications/c.md", GitHubFileStatus.Added),
            ("deadlines.csv", GitHubFileStatus.Modified)
        }));
    }

    [Test]
    public async Task CloudAgentBranch_ComparedToMain()
    {
        _github.CreateBranch("claude/open-calls");
        _github.Commit("a.md", "a", branch: "claude/open-calls", author: "Claude");
        _github.Commit("b.md", "b", branch: "claude/open-calls", author: "Claude");

        var branches = await _repository.GetBranchesAsync();
        var comparison = await _repository.CompareAsync("main", "claude/open-calls");
        var agentFiles = await _github.CreateRepository("claude/open-calls").CreateProvider()
            .GetItemsAsync(FileProviderFilter.Default).ToListAsync();

        Assert.That(branches.Select(b => b.Name), Does.Contain("claude/open-calls"));
        Assert.That((comparison.Status, comparison.AheadBy, comparison.BehindBy), Is.EqualTo(("ahead", 2, 0)));
        Assert.That(agentFiles.Select(f => f.FullName), Does.Contain("github:anna/art-life@claude/open-calls:/a.md"));
    }

    [Test]
    public async Task Info_DefaultBranchAndPrivacy()
    {
        var info = await _repository.GetInfoAsync();

        Assert.That(info, Is.EqualTo(new GitHubRepositoryInfo("main", true)));
    }

    private sealed class MemoryFile(string name, byte[] content) : IFileItem
    {
        public string Name => name;
        public string FullName => "/tmp/" + name;
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        public long Size => content.Length;
        long? IFileProviderItem.Size => content.Length;
        public string Extension => Path.GetExtension(name);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
            Task.FromResult<(Stream, long)>((new MemoryStream(content), content.Length));

        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
