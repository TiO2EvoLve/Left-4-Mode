using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Left_4_Mod.Models;
using Left_4_Mod.Services;
using Left_4_Mod.ViewModels;
using Xunit;

namespace Left4Mod.Tests;

public sealed class ModLibraryTests
{
    [Fact]
    public async Task Scan_lists_folders_and_vpk_mods()
    {
        using var libraryDirectory = new TempDirectory();
        File.WriteAllText(libraryDirectory.Combine("ak47.vpk"), "vpk");
        Directory.CreateDirectory(libraryDirectory.Combine("weapons"));

        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);

        var entries = await library.ScanAsync(string.Empty, default);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.IsFolder && entry.Name == "weapons");
        Assert.Contains(entries, entry => entry.IsMod && entry.Name == "ak47.vpk");
    }

    [Fact]
    public async Task Paths_outside_the_library_are_rejected()
    {
        using var libraryDirectory = new TempDirectory();
        var library = CreateLibrary(libraryDirectory.Path, new RecordingLog());

        Assert.Throws<LibraryPathException>(() => library.GetFullPath("..\\outside"));
        Assert.Throws<LibraryPathException>(() => library.GetFullPath(Path.GetTempPath()));
        Assert.Throws<LibraryPathException>(() => library.SetLibrary(Path.GetPathRoot(Path.GetTempPath())!));
        await Assert.ThrowsAsync<LibraryPathException>(() => library.ScanAsync("..", default));
    }

    [Fact]
    public async Task Create_and_rename_folders_use_unique_names()
    {
        using var libraryDirectory = new TempDirectory();
        var library = CreateLibrary(libraryDirectory.Path, new RecordingLog());

        var first = await library.CreateFolderAsync(string.Empty, "新建文件夹", default);
        var second = await library.CreateFolderAsync(string.Empty, "新建文件夹", default);

        Assert.Equal("新建文件夹", first.Name);
        Assert.Equal("新建文件夹 (2)", second.Name);

        var folders = await library.EnumerateFoldersAsync();
        Assert.Contains("新建文件夹", folders);
        Assert.Contains("新建文件夹 (2)", folders);

        var renamed = await library.RenameAsync(first.RelativePath, "武器", default);
        Assert.Equal("武器", renamed);
        Assert.True(Directory.Exists(libraryDirectory.Combine("武器")));

        await Assert.ThrowsAsync<LibraryPathException>(() =>
            library.RenameAsync(renamed, "新建文件夹 (2)", default));
    }

    [Fact]
    public async Task Delete_moves_items_to_recycle_bin_and_counts_children()
    {
        using var libraryDirectory = new TempDirectory();
        var library = CreateLibrary(libraryDirectory.Path, new RecordingLog());

        var folder = await library.CreateFolderAsync(string.Empty, "分类", default);
        File.WriteAllText(libraryDirectory.Combine("分类\\a.txt"), "a");
        Directory.CreateDirectory(libraryDirectory.Combine("分类\\sub"));
        File.WriteAllText(libraryDirectory.Combine("分类\\sub\\b.txt"), "b");

        var info = await library.GetDeleteInfoAsync([folder.RelativePath], default);
        Assert.Equal(2, info.FileCount);
        Assert.Equal(1, info.FolderCount);
        Assert.True(info.ContainsChildren);

        await library.DeleteAsync([folder.RelativePath], default);
        Assert.False(Directory.Exists(libraryDirectory.Combine("分类")));
    }

    [Fact]
    public async Task Import_copies_sources_and_auto_renames_conflicts()
    {
        using var libraryDirectory = new TempDirectory();
        using var sourceDirectory = new TempDirectory();
        var library = CreateLibrary(libraryDirectory.Path, new RecordingLog());

        var source = sourceDirectory.Combine("mod.vpk");
        File.WriteAllText(source, "source-content");

        var plan = library.CreateImportPlan(string.Empty, [source]);
        Assert.Single(plan.Items);
        Assert.Equal(0, plan.RenamedCount);

        var outcome = await library.ImportAsync(plan, null, default);
        Assert.Single(outcome.ImportedRelativePaths);
        Assert.True(File.Exists(libraryDirectory.Combine("mod.vpk")));
        Assert.True(File.Exists(source), "导入不得移动或删除源文件");

        var secondPlan = library.CreateImportPlan(string.Empty, [source]);
        Assert.Equal(1, secondPlan.RenamedCount);

        await library.ImportAsync(secondPlan, null, default);
        Assert.True(File.Exists(libraryDirectory.Combine("mod (2).vpk")));
        Assert.Empty(Directory.GetFiles(libraryDirectory.Path, ".l4m-import-*"));
    }

    [Fact]
    public async Task Copy_and_move_stay_inside_the_library()
    {
        using var libraryDirectory = new TempDirectory();
        var library = CreateLibrary(libraryDirectory.Path, new RecordingLog());

        var sourceFolder = await library.CreateFolderAsync(string.Empty, "a", default);
        var targetFolder = await library.CreateFolderAsync(string.Empty, "b", default);
        File.WriteAllText(libraryDirectory.Combine("a\\file.vpk"), "content");
        Directory.CreateDirectory(libraryDirectory.Combine("a\\b"));

        var moved = await library.MoveAsync(["a\\file.vpk"], targetFolder.RelativePath, default);
        Assert.Single(moved);
        Assert.False(File.Exists(libraryDirectory.Combine("a\\file.vpk")));
        Assert.True(File.Exists(libraryDirectory.Combine("b\\file.vpk")));

        var copied = await library.CopyAsync(["b\\file.vpk"], string.Empty, default);
        Assert.Single(copied);
        Assert.True(File.Exists(libraryDirectory.Combine("file.vpk")));

        var sameFolderCopy = await library.CopyAsync(["b\\file.vpk"], "b", default);
        Assert.Single(sameFolderCopy);
        Assert.True(File.Exists(libraryDirectory.Combine("b\\file - 副本.vpk")));

        await Assert.ThrowsAsync<LibraryPathException>(() =>
            library.MoveAsync([sourceFolder.RelativePath], "a\\b", default));
    }

    [Fact]
    public async Task Settings_round_trip_and_corrupt_file_is_backed_up()
    {
        using var directory = new TempDirectory();
        var settingsPath = directory.Combine("settings.json");
        var log = new RecordingLog();
        var settings = new JsonSettingsService(log) { SettingsFilePath = settingsPath };

        await settings.SaveAsync(new AppSettings
        {
            LibraryPath = directory.Path,
            LastFolder = "weapons",
        });

        var loaded = settings.Load();
        Assert.Equal(directory.Path, loaded.LibraryPath);
        Assert.Equal("weapons", loaded.LastFolder);

        File.WriteAllText(settingsPath, "{ this is not json");
        var fallback = settings.Load();

        Assert.Null(fallback.LibraryPath);
        Assert.True(Directory.GetFiles(directory.Path, "settings.json.broken-*").Length == 1);
    }

    [Fact]
    public async Task ViewModel_loads_mods_applies_filters_and_persists_enabled_state()
    {
        using var libraryDirectory = new TempDirectory();
        using var appDataDirectory = new TempDirectory();
        Directory.CreateDirectory(libraryDirectory.Combine("weapons"));
        Directory.CreateDirectory(libraryDirectory.Combine("characters"));
        File.WriteAllText(libraryDirectory.Combine("weapons\\ak47.vpk"), "vpk");
        File.WriteAllText(libraryDirectory.Combine("characters\\zoey.vpk"), "vpk");

        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var settings = new JsonSettingsService(log)
        {
            SettingsFilePath = appDataDirectory.Combine("settings.json"),
        };
        var stateStore = new JsonModStateStore(log)
        {
            StateDirectory = appDataDirectory.Combine("state"),
        };
        var deployment = new DeploymentService(library, log)
        {
            ManifestFilePath = appDataDirectory.Combine("deployments.json"),
        };
        var catalogService = new ModCatalogService(library, stateStore, deployment, log);
        var viewModel = new MainViewModel(
            library,
            catalogService,
            deployment,
            new GamePathService(log),
            new NoopWorkshopService(),
            new StubThumbnailService(),
            settings,
            log);

        await viewModel.SetLibraryAsync(libraryDirectory.Path);

        Assert.True(viewModel.HasLibrary);
        Assert.Equal(2, viewModel.ModCount);
        Assert.Empty(viewModel.Mods);
        Assert.Contains(viewModel.Rows, row => row.IsFolder && row.RelativePath == "weapons");
        Assert.Contains(viewModel.Rows, row => row.IsFolder && row.RelativePath == "characters");

        viewModel.NavigateTo("weapons");
        Assert.Equal("weapons", viewModel.CurrentFolderPath);
        Assert.Equal("weapons", viewModel.Breadcrumbs.Last().RelativePath);

        var ak47 = viewModel.Mods.Single(item => item.FileName == "ak47.vpk");
        viewModel.SetSelection([ak47]);
        await viewModel.SetSelectedEnabledAsync(true);

        Assert.True(ak47.IsEnabled);
        Assert.Equal(1, viewModel.EnabledCount);
        Assert.Equal(1, viewModel.PendingCount);

        var persisted = await stateStore.LoadAsync(libraryDirectory.Path);
        Assert.True(persisted.Mods.Single(record => record.RelativePath == "weapons\\ak47.vpk").IsEnabled);

        viewModel.NavigateTo("characters");
        viewModel.SearchText = "zoey";
        Assert.Single(viewModel.Mods);
        Assert.Equal("characters", viewModel.Mods[0].GroupPath);

        viewModel.ClearFilters();
        Assert.Single(viewModel.Mods);

        viewModel.NavigateTo("weapons");
        viewModel.SelectedEnabledFilter = viewModel.EnabledFilterOptions
            .Single(option => option.Value == ModEnabledFilter.Enabled);
        Assert.Single(viewModel.Mods);
        Assert.True(viewModel.Mods[0].IsEnabled);

        var gameRoot = System.IO.Path.Combine(appDataDirectory.Path, "game");
        Directory.CreateDirectory(System.IO.Path.Combine(gameRoot, "left4dead2", "addons"));

        var (settingsApplied, settingsError) = await viewModel.ApplySettingsAsync(
            libraryDirectory.Path,
            gameRoot);

        Assert.True(settingsApplied, settingsError);
        Assert.True(viewModel.IsGameReady);
        Assert.Equal(gameRoot, settings.Load().GamePath);
    }

    [Fact]
    public async Task ViewModel_settings_survive_reload()
    {
        using var libraryDirectory = new TempDirectory();
        using var appDataDirectory = new TempDirectory();
        var gameRoot = appDataDirectory.Combine("game");
        Directory.CreateDirectory(libraryDirectory.Combine("weapons"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "left4dead2", "addons"));
        File.WriteAllText(libraryDirectory.Combine("weapons\\ak47.vpk"), "vpk");

        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var settingsPath = appDataDirectory.Combine("settings.json");
        var stateDirectory = appDataDirectory.Combine("state");
        var manifestPath = appDataDirectory.Combine("deployments.json");

        var settings = new JsonSettingsService(log) { SettingsFilePath = settingsPath };
        var stateStore = new JsonModStateStore(log) { StateDirectory = stateDirectory };
        var deployment = new DeploymentService(library, log) { ManifestFilePath = manifestPath };
        var catalogService = new ModCatalogService(library, stateStore, deployment, log);

        var first = new MainViewModel(
            library,
            catalogService,
            deployment,
            new GamePathService(log),
            new NoopWorkshopService(),
            new StubThumbnailService(),
            settings,
            log);

        await first.SetLibraryAsync(libraryDirectory.Path);
        var (applied, error) = await first.ApplySettingsAsync(libraryDirectory.Path, gameRoot);
        Assert.True(applied, error);

        var second = new MainViewModel(
            library,
            catalogService,
            deployment,
            new GamePathService(log),
            new NoopWorkshopService(),
            new StubThumbnailService(),
            new JsonSettingsService(log) { SettingsFilePath = settingsPath },
            log);

        Assert.True(second.HasLibrary);
        Assert.Equal(Path.GetFullPath(libraryDirectory.Path), Path.GetFullPath(second.LibraryPath));
        Assert.True(second.IsGameReady);
        Assert.Equal(gameRoot, second.GamePath);
    }

    [Fact]
    public async Task Folder_navigation_and_recursive_delete_update_state()
    {
        using var libraryDirectory = new TempDirectory();
        using var appDataDirectory = new TempDirectory();
        Directory.CreateDirectory(libraryDirectory.Combine("分类\\子分类"));
        File.WriteAllText(libraryDirectory.Combine("分类\\子分类\\a.vpk"), "vpk");

        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var settings = new JsonSettingsService(log)
        {
            SettingsFilePath = appDataDirectory.Combine("settings.json"),
        };
        var stateStore = new JsonModStateStore(log)
        {
            StateDirectory = appDataDirectory.Combine("state"),
        };
        var deployment = new DeploymentService(library, log)
        {
            ManifestFilePath = appDataDirectory.Combine("deployments.json"),
        };
        var catalogService = new ModCatalogService(library, stateStore, deployment, log);
        var viewModel = new MainViewModel(
            library,
            catalogService,
            deployment,
            new GamePathService(log),
            new NoopWorkshopService(),
            new StubThumbnailService(),
            settings,
            log);

        await viewModel.SetLibraryAsync(libraryDirectory.Path);

        Assert.Contains(viewModel.Rows, row =>
            row.IsFolder && string.Equals(row.RelativePath, "分类", StringComparison.OrdinalIgnoreCase));

        viewModel.NavigateTo("分类");
        Assert.Contains(viewModel.Rows, row =>
            row.IsFolder && string.Equals(row.RelativePath, "分类\\子分类", StringComparison.OrdinalIgnoreCase));

        viewModel.NavigateTo("分类\\子分类");
        Assert.Contains(viewModel.Rows, row => row.Mod?.FileName == "a.vpk");
        Assert.Equal(3, viewModel.Breadcrumbs.Count);

        viewModel.NavigateBack();
        Assert.Equal("分类", viewModel.CurrentFolderPath);

        viewModel.NavigateTo(string.Empty);
        var folderRow = viewModel.Rows.Single(row =>
            row.IsFolder && string.Equals(row.RelativePath, "分类", StringComparison.OrdinalIgnoreCase));

        var deleteResult = await viewModel.DeleteFolderAsync(folderRow);
        Assert.True(deleteResult.Success, deleteResult.Message);
        Assert.False(Directory.Exists(libraryDirectory.Combine("分类")));

        var state = await stateStore.LoadAsync(libraryDirectory.Path);
        Assert.Empty(state.Mods);
    }

    [Fact]
    public async Task Deployment_adds_updates_removes_and_protects_unregistered_files()
    {
        using var libraryDirectory = new TempDirectory();
        using var addonsDirectory = new TempDirectory();
        using var appDataDirectory = new TempDirectory();

        Directory.CreateDirectory(libraryDirectory.Combine("weapons"));
        File.WriteAllText(libraryDirectory.Combine("weapons\\ak47.vpk"), "vpk-content");

        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var deployment = new DeploymentService(library, log)
        {
            ManifestFilePath = appDataDirectory.Combine("deployments.json"),
        };

        var manifest = new DeploymentManifest();
        var record = new ModRecord
        {
            Id = "mod-ak47",
            RelativePath = "weapons\\ak47.vpk",
            IsEnabled = true,
        };

        var userFile = addonsDirectory.Combine("user-addon.vpk");
        File.WriteAllText(userFile, "user-data");

        var plan = deployment.CreatePlan([record], manifest, addonsDirectory.Path);
        Assert.Equal(1, plan.AddCount);
        Assert.Equal(0, plan.ConflictCount);

        var result = await deployment.ApplyAsync(plan, null, CancellationToken.None);
        Assert.Equal(1, result.Added);
        Assert.Empty(result.Failures);

        var deployedFile = addonsDirectory.Combine($"l4m_{record.Id}.vpk");
        Assert.True(File.Exists(deployedFile));
        Assert.True(File.Exists(userFile));
        Assert.Equal("vpk-content", File.ReadAllText(deployedFile));
        Assert.True((File.GetAttributes(deployedFile) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal(ModDeploymentState.Deployed, deployment.GetStatus(manifest, addonsDirectory.Path, record));

        var keepPlan = deployment.CreatePlan([record], manifest, addonsDirectory.Path);
        Assert.Equal(1, keepPlan.KeepCount);
        Assert.False(keepPlan.HasChanges);

        // 旧版本可能留下的是普通副本，下一次推送应迁移为符号链接。
        File.Delete(deployedFile);
        File.WriteAllText(deployedFile, "legacy-copy");
        var migratePlan = deployment.CreatePlan([record], manifest, addonsDirectory.Path);
        Assert.Equal(1, migratePlan.UpdateCount);
        await deployment.ApplyAsync(migratePlan, null, CancellationToken.None);
        Assert.True((File.GetAttributes(deployedFile) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal("vpk-content", File.ReadAllText(deployedFile));

        record.IsEnabled = false;
        var removePlan = deployment.CreatePlan([record], manifest, addonsDirectory.Path);
        Assert.Equal(1, removePlan.RemoveCount);

        var removeResult = await deployment.ApplyAsync(removePlan, null, CancellationToken.None);
        Assert.Equal(1, removeResult.Removed);
        Assert.False(File.Exists(deployedFile));
        Assert.True(File.Exists(userFile));
        Assert.Equal("user-data", File.ReadAllText(userFile));
    }

    [Fact]
    public async Task Deployment_reports_conflicts_without_overwriting()
    {
        using var libraryDirectory = new TempDirectory();
        using var addonsDirectory = new TempDirectory();
        using var appDataDirectory = new TempDirectory();

        File.WriteAllText(libraryDirectory.Combine("mod.vpk"), "vpk-content");
        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var deployment = new DeploymentService(library, log)
        {
            ManifestFilePath = appDataDirectory.Combine("deployments.json"),
        };

        var record = new ModRecord
        {
            Id = "conflict-id",
            RelativePath = "mod.vpk",
            IsEnabled = true,
        };

        var occupied = addonsDirectory.Combine("l4m_conflict-id.vpk");
        File.WriteAllText(occupied, "belongs-to-user");

        var plan = deployment.CreatePlan([record], new DeploymentManifest(), addonsDirectory.Path);
        Assert.Equal(1, plan.ConflictCount);
        Assert.False(plan.HasChanges);

        var result = await deployment.ApplyAsync(plan, null, CancellationToken.None);
        Assert.Equal(1, result.Skipped);
        Assert.Single(result.Failures);
        Assert.Equal("belongs-to-user", File.ReadAllText(occupied));
    }

    [Fact]
    public async Task Deployment_keeps_symlink_across_source_changes()
    {
        using var libraryDirectory = new TempDirectory();
        using var addonsDirectory = new TempDirectory();
        using var appDataDirectory = new TempDirectory();

        var source = libraryDirectory.Combine("mod.vpk");
        File.WriteAllText(source, "content-v1");
        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var deployment = new DeploymentService(library, log)
        {
            ManifestFilePath = appDataDirectory.Combine("deployments.json"),
        };

        var record = new ModRecord
        {
            Id = "symlink-id",
            RelativePath = "mod.vpk",
            IsEnabled = true,
        };

        var manifest = new DeploymentManifest();
        var plan = deployment.CreatePlan([record], manifest, addonsDirectory.Path);
        await deployment.ApplyAsync(plan, null, CancellationToken.None);

        var deployed = addonsDirectory.Combine("l4m_symlink-id.vpk");
        Assert.Equal("content-v1", File.ReadAllText(deployed));

        File.WriteAllText(source, "content-v2");

        Assert.Equal("content-v2", File.ReadAllText(deployed));
        Assert.Equal(ModDeploymentState.Deployed, deployment.GetStatus(manifest, addonsDirectory.Path, record));

        var keepPlan = deployment.CreatePlan([record], manifest, addonsDirectory.Path);
        Assert.Equal(1, keepPlan.KeepCount);
        Assert.False(keepPlan.HasChanges);
    }

    [Fact]
    public async Task Deployment_retargets_and_removes_moved_mods()
    {
        using var libraryDirectory = new TempDirectory();
        using var addonsDirectory = new TempDirectory();
        using var appDataDirectory = new TempDirectory();

        var source = libraryDirectory.Combine("mod.vpk");
        File.WriteAllText(source, "moved-content");
        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var deployment = new DeploymentService(library, log)
        {
            ManifestFilePath = appDataDirectory.Combine("deployments.json"),
        };

        var record = new ModRecord
        {
            Id = "move-id",
            RelativePath = "mod.vpk",
            IsEnabled = true,
        };

        var plan = deployment.CreatePlan([record], new DeploymentManifest(), addonsDirectory.Path);
        await deployment.ApplyAsync(plan, null, CancellationToken.None);
        var deployed = addonsDirectory.Combine("l4m_move-id.vpk");

        Directory.CreateDirectory(libraryDirectory.Combine("分类"));
        var movedSource = libraryDirectory.Combine("分类\\mod.vpk");
        File.Move(source, movedSource);
        record.RelativePath = "分类\\mod.vpk";

        await deployment.UpdateModSourcesAsync([record]);

        var target = File.ResolveLinkTarget(deployed, returnFinalTarget: true);
        Assert.NotNull(target);
        Assert.Equal(Path.GetFullPath(movedSource), target!.FullName);
        Assert.Equal("moved-content", File.ReadAllText(deployed));

        await deployment.RemoveModsAsync([record.Id]);

        Assert.False(File.Exists(deployed));
        Assert.True(File.Exists(movedSource));
    }

    [Fact]
    public async Task Workshop_add_downloads_vpk_and_registers_record()
    {
        using var libraryDirectory = new TempDirectory();
        using var stateDirectory = new TempDirectory();
        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var stateStore = new JsonModStateStore(log) { StateDirectory = stateDirectory.Path };

        using var httpClient = new HttpClient(new StubHttpMessageHandler(CreateWorkshopJson(
            id: 12345,
            title: "Test Workshop Mod",
            timeUpdated: 100,
            appId: 550)));
        var downloader = new FakeWorkshopDownloader("workshop-content");
        var service = new WorkshopService(httpClient, downloader, library, stateStore, log);

        var result = await service.AddAsync(12345, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Workshop\\12345.vpk", result.RelativePath);
        Assert.True(File.Exists(libraryDirectory.Combine("Workshop\\12345.vpk")));
        Assert.Equal("workshop-content", File.ReadAllText(libraryDirectory.Combine("Workshop\\12345.vpk")));
        Assert.Equal("https://example.com/mod.vpk", downloader.LastUrl);

        var state = await stateStore.LoadAsync(libraryDirectory.Path);
        var record = Assert.Single(state.Mods);
        Assert.Equal(12345ul, record.WorkshopId);
        Assert.Equal("Test Workshop Mod", record.DisplayName);
        Assert.Equal("Workshop", record.GroupPath);
        Assert.False(record.IsEnabled);
    }

    [Fact]
    public async Task Workshop_auto_update_replaces_vpk_in_place_and_updates_time()
    {
        using var libraryDirectory = new TempDirectory();
        using var stateDirectory = new TempDirectory();
        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var stateStore = new JsonModStateStore(log) { StateDirectory = stateDirectory.Path };

        Directory.CreateDirectory(libraryDirectory.Combine("Workshop"));
        File.WriteAllText(libraryDirectory.Combine("Workshop\\12345.vpk"), "old-content");

        var state = new ModState
        {
            LibraryPath = libraryDirectory.Path,
            Mods =
            [
                new ModRecord
                {
                    Id = "workshop-12345",
                    RelativePath = "Workshop\\12345.vpk",
                    GroupPath = "Workshop",
                    DisplayName = "Old Title",
                    WorkshopId = 12345,
                    WorkshopTitle = "Old Title",
                    WorkshopTimeUpdated = 100,
                    WorkshopAutoUpdate = true,
                    IsEnabled = true,
                },
            ],
        };
        await stateStore.SaveAsync(state);

        using var httpClient = new HttpClient(new StubHttpMessageHandler(CreateWorkshopJson(
            id: 12345,
            title: "New Title",
            timeUpdated: 200,
            appId: 550)));
        var downloader = new FakeWorkshopDownloader("new-content");
        var service = new WorkshopService(httpClient, downloader, library, stateStore, log);
        var catalog = new ModCatalog(state, [], ["Workshop"], new DeploymentManifest());

        var result = await service.CheckForUpdatesAsync(catalog, null, CancellationToken.None);

        Assert.Single(result.Updated);
        Assert.Empty(result.Failures);
        Assert.Equal(100ul, result.Updated[0].OldTimeUpdated);
        Assert.Equal(200ul, result.Updated[0].NewTimeUpdated);
        Assert.Equal("new-content", File.ReadAllText(libraryDirectory.Combine("Workshop\\12345.vpk")));

        var record = Assert.Single(state.Mods);
        Assert.Equal(200ul, record.WorkshopTimeUpdated);
        Assert.Equal("New Title", record.DisplayName);
        Assert.True(record.IsEnabled);
    }

    [Fact]
    public async Task Workshop_rejects_non_l4d2_items()
    {
        using var libraryDirectory = new TempDirectory();
        using var stateDirectory = new TempDirectory();
        var log = new RecordingLog();
        var library = CreateLibrary(libraryDirectory.Path, log);
        var stateStore = new JsonModStateStore(log) { StateDirectory = stateDirectory.Path };

        using var httpClient = new HttpClient(new StubHttpMessageHandler(CreateWorkshopJson(
            id: 999,
            title: "Other Game",
            timeUpdated: 100,
            appId: 440)));
        var service = new WorkshopService(
            httpClient,
            new FakeWorkshopDownloader("x"),
            library,
            stateStore,
            log);

        var result = await service.AddAsync(999, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("求生之路 2", result.Error);
        Assert.False(File.Exists(libraryDirectory.Combine("Workshop\\999.vpk")));
    }

    [Fact]
    public void Workshop_id_parser_accepts_numbers_and_links()
    {
        Assert.True(WorkshopUtils.TryParsePublishedFileId("123456789", out var numericId));
        Assert.Equal(123456789ul, numericId);

        Assert.True(WorkshopUtils.TryParsePublishedFileId(
            "https://steamcommunity.com/sharedfiles/filedetails/?id=987654321&searchtext=",
            out var linkId));
        Assert.Equal(987654321ul, linkId);

        Assert.False(WorkshopUtils.TryParsePublishedFileId("not-an-id", out _));
    }

    private static string CreateWorkshopJson(ulong id, string title, ulong timeUpdated, int appId) =>
        $$"""
        {
          "response": {
            "result": 1,
            "publishedfiledetails": [
              {
                "result": 1,
                "publishedfileid": "{{id}}",
                "consumer_app_id": {{appId}},
                "title": "{{title}}",
                "description": "description",
                "file_url": "https://example.com/mod.vpk",
                "preview_url": "https://example.com/preview.jpg",
                "time_created": 1,
                "time_updated": {{timeUpdated}},
                "file_size": "15"
              }
            ]
          }
        }
        """;

    [Fact]
    public async Task Mod_state_store_round_trips_per_library()
    {
        using var libraryDirectory = new TempDirectory();
        using var stateDirectory = new TempDirectory();
        var log = new RecordingLog();
        var store = new JsonModStateStore(log) { StateDirectory = stateDirectory.Path };

        var state = new ModState
        {
            LibraryPath = libraryDirectory.Path,
            Mods =
            [
                new ModRecord
                {
                    Id = "mod-1",
                    RelativePath = "weapons\\ak47.vpk",
                    GroupPath = "weapons",
                    IsEnabled = true,
                },
            ],
        };

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync(libraryDirectory.Path);

        Assert.Equal(libraryDirectory.Path, loaded.LibraryPath);
        Assert.Single(loaded.Mods);
        Assert.True(loaded.Mods[0].IsEnabled);
        Assert.Equal("weapons", loaded.Mods[0].GroupPath);
    }

    [Fact]
    public void Game_path_resolves_root_left4dead2_and_addons()
    {
        using var directory = new TempDirectory();
        var gameRoot = directory.Combine("Left 4 Dead 2");
        var left4Dead2 = System.IO.Path.Combine(gameRoot, "left4dead2");
        var addons = System.IO.Path.Combine(left4Dead2, "addons");
        Directory.CreateDirectory(addons);

        var service = new GamePathService(new RecordingLog());

        var fromRoot = service.TryResolve(gameRoot, out var rootError);
        Assert.NotNull(fromRoot);
        Assert.Equal(addons, fromRoot!.AddonsPath);
        Assert.Equal(string.Empty, rootError);

        var fromLeft4Dead2 = service.TryResolve(left4Dead2, out _);
        Assert.NotNull(fromLeft4Dead2);
        Assert.Equal(addons, fromLeft4Dead2!.AddonsPath);

        var fromAddons = service.TryResolve(addons, out _);
        Assert.NotNull(fromAddons);
        Assert.Equal(addons, fromAddons!.AddonsPath);

        var invalid = service.TryResolve(directory.Path, out var invalidError);
        Assert.Null(invalid);
        Assert.NotEmpty(invalidError);
    }

    private static ModLibraryService CreateLibrary(string rootPath, ILogService log)
    {
        var library = new ModLibraryService(log);
        library.SetLibrary(rootPath);
        return library;
    }

    private sealed class RecordingLog : ILogService
    {
        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);

        public void Warn(string message) => Messages.Add(message);

        public void Error(Exception exception, string message) => Messages.Add($"{message}: {exception.Message}");
    }

    private sealed class NoopWorkshopService : IWorkshopService
    {
        public Task<WorkshopItemInfo> GetDetailsAsync(ulong publishedFileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkshopAddResult> AddAsync(
            ulong publishedFileId,
            IProgress<WorkshopProgress>? progress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WorkshopAddResult.Fail("not used"));

        public Task<WorkshopUpdateResult> CheckForUpdatesAsync(
            ModCatalog catalog,
            IProgress<WorkshopProgress>? progress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkshopUpdateResult(0, [], [], false));
    }

    private sealed class StubThumbnailService : IModThumbnailService
    {
        public Bitmap? FolderImage => null;

        public Bitmap? PlaceholderImage => null;

        public Task<Bitmap?> LoadAsync(
            ModRecord record,
            string vpkFullPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Bitmap?>(null);
    }

    private sealed class FakeWorkshopDownloader : IWorkshopDownloader    {
        private readonly string _content;

        public FakeWorkshopDownloader(string content)
        {
            _content = content;
        }

        public string? LastUrl { get; private set; }

        public Task DownloadAsync(
            string url,
            string destinationPath,
            IProgress<WorkshopDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastUrl = url;
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(destinationPath, _content);
            progress?.Report(new WorkshopDownloadProgress(
                Encoding.UTF8.GetByteCount(_content),
                Encoding.UTF8.GetByteCount(_content),
                0,
                100));
            return Task.CompletedTask;
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHttpMessageHandler(string json)
        {
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "l4mod-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(string relativePath) =>
            System.IO.Path.Combine(Path, relativePath);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // 测试清理失败不应掩盖断言结果。
            }
        }
    }
}
