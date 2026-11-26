namespace RevitAgent.Host.ViewModels
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Linq;
    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.Input;
    using RevitAgent.Core.Revit;
    using RevitAgent.Host.Services;

    /// <summary>One line of the allowlist grid. Toggling <see cref="IsEnabled"/> saves at once.</summary>
    public sealed partial class AddinAllowlistRow : ObservableObject
    {
        public AddinAllowlistRow(AddinAllowlistEntry entry)
        {
            Entry = entry;
            _isEnabled = entry.Enabled;
        }

        public AddinAllowlistEntry Entry { get; }

        public string Id => Entry.Id;

        public string Name => Entry.Name;

        public string PathPattern => Entry.PathPattern;

        public string Note => Entry.Note;

        public string AddedAtDisplay => Entry.AddedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

        [ObservableProperty]
        private bool _isEnabled;
    }

    /// <summary>
    /// Dashboard page "Allowed add-ins": the owner's allowlist of unsigned add-ins. This is the ONLY writer of
    /// <c>addin-allowlist.json</c> (no MCP tool can change it).
    /// </summary>
    public partial class AddinAllowlistViewModel : ObservableObject
    {
        private readonly AddinAllowlistStore _store;
        private readonly ILocalizationService _localization;
        private bool _loading;
        private bool _refreshing;

        public AddinAllowlistViewModel(AddinAllowlistStore store, ILocalizationService localization)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _localization = localization ?? throw new ArgumentNullException(nameof(localization));
            Reload();
        }

        public ObservableCollection<AddinAllowlistRow> Items { get; } = new();

        /// <summary>The folder segments of the typed path that can be turned into a "*" (empty when the path is not a plain file path).</summary>
        public ObservableCollection<string> FolderSegments { get; } = new();

        /// <summary>Browse dialog: returns the chosen DLL path or null. Replaceable in tests.</summary>
        public Func<string?> BrowseFile { get; set; } = DefaultBrowse;

        /// <summary>Asks before removing an entry. Replaceable in tests.</summary>
        public Func<string, bool> ConfirmRemove { get; set; } = _ => true;

        public string FilePath => _store.FilePath;

        [ObservableProperty]
        private string _editName = string.Empty;

        [ObservableProperty]
        private string _editPath = string.Empty;

        [ObservableProperty]
        private string _editNote = string.Empty;

        [ObservableProperty]
        private bool _useFolderPattern;

        /// <summary>Index into <see cref="FolderSegments"/> of the folder replaced by "*" (-1 = none).</summary>
        [ObservableProperty]
        private int _patternSegmentIndex = -1;

        /// <summary>The pattern that will be saved (the typed path, or the path with one folder replaced by "*").</summary>
        [ObservableProperty]
        private string _patternPreview = string.Empty;

        [ObservableProperty]
        private string? _editingId;

        [ObservableProperty]
        private string _message = string.Empty;

        [ObservableProperty]
        private bool _messageIsError;

        [ObservableProperty]
        private string? _fileError;

        public bool IsEditing => EditingId is not null;

        partial void OnEditingIdChanged(string? value) => OnPropertyChanged(nameof(IsEditing));

        partial void OnEditPathChanged(string value) => RefreshPattern(resetSegment: true);

        partial void OnUseFolderPatternChanged(bool value) => RefreshPattern(resetSegment: value);

        partial void OnPatternSegmentIndexChanged(int value) => RefreshPattern(resetSegment: false);

        /// <summary>Re-reads the file (also picks up hand edits).</summary>
        [RelayCommand]
        public void Reload()
        {
            var snap = _store.Load();
            _loading = true;
            try
            {
                Items.Clear();
                foreach (var e in snap.Entries)
                {
                    var row = new AddinAllowlistRow(e);
                    row.PropertyChanged += OnRowChanged;
                    Items.Add(row);
                }
            }
            finally
            {
                _loading = false;
            }

            FileError = snap.Error;
        }

        [RelayCommand]
        public void Browse()
        {
            var path = BrowseFile();
            if (!string.IsNullOrWhiteSpace(path))
            {
                EditPath = path;
            }
        }

        [RelayCommand]
        public void Save()
        {
            var name = EditName.Trim();
            var pattern = PatternPreview.Trim();
            if (name.Length == 0)
            {
                SetMessage("Str_AddinErrName", error: true);
                return;
            }

            if (AddinAllowlistPolicy.ValidatePattern(pattern) is { } problem)
            {
                SetMessage(_localization.GetString("Str_AddinErrPattern") + " " + problem, error: true, literal: true);
                return;
            }

            try
            {
                if (EditingId is { } id)
                {
                    var row = Items.FirstOrDefault(r => r.Id == id);
                    var updated = (row?.Entry ?? new AddinAllowlistEntry { Id = id }) with
                    {
                        Name = name,
                        PathPattern = pattern,
                        Note = EditNote.Trim(),
                        Enabled = row?.IsEnabled ?? true,
                    };
                    if (!_store.Update(updated))
                    {
                        SetMessage("Str_AddinErrGone", error: true);
                        Reload();
                        return;
                    }
                }
                else
                {
                    _store.Add(new AddinAllowlistEntry { Name = name, PathPattern = pattern, Note = EditNote.Trim(), Enabled = true, AddedAt = DateTimeOffset.Now });
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                SetMessage(ex.Message, error: true, literal: true);
                return;
            }

            Reload();
            ClearForm();
            SetMessage("Str_AddinSaved", error: false);
        }

        [RelayCommand]
        public void Edit(AddinAllowlistRow? row)
        {
            if (row is null)
            {
                return;
            }

            ClearForm();
            EditingId = row.Id;
            EditName = row.Name;
            EditNote = row.Note;
            EditPath = row.PathPattern;
        }

        [RelayCommand]
        public void ClearForm()
        {
            EditingId = null;
            EditName = string.Empty;
            EditNote = string.Empty;
            UseFolderPattern = false;
            EditPath = string.Empty;
            Message = string.Empty;
            MessageIsError = false;
        }

        [RelayCommand]
        public void Remove(AddinAllowlistRow? row)
        {
            if (row is null || !ConfirmRemove(row.Name))
            {
                return;
            }

            try
            {
                _store.Remove(row.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                SetMessage(ex.Message, error: true, literal: true);
                return;
            }

            if (EditingId == row.Id)
            {
                ClearForm();
            }

            Reload();
            SetMessage("Str_AddinRemoved", error: false);
        }

        private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_loading || sender is not AddinAllowlistRow row || e.PropertyName != nameof(AddinAllowlistRow.IsEnabled))
            {
                return;
            }

            try
            {
                _store.SetEnabled(row.Id, row.IsEnabled);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                SetMessage(ex.Message, error: true, literal: true);
                Reload();
            }
        }

        private void RefreshPattern(bool resetSegment)
        {
            if (_refreshing)
            {
                return; // the ComboBox rewrites its selection while the list is rebuilt
            }

            _refreshing = true;
            try
            {
                var path = EditPath.Trim();
                var segments = UseFolderPattern ? FolderSegmentsOf(path) : new List<string>();

                if (!FolderSegments.SequenceEqual(segments))
                {
                    FolderSegments.Clear();
                    foreach (var s in segments)
                    {
                        FolderSegments.Add(s);
                    }

                    resetSegment = true;
                }

                if (resetSegment || PatternSegmentIndex >= segments.Count)
                {
                    // Default: the folder that contains the file's own folder (e.g. the worktree in ...\<worktree>\Outcome\X.dll).
                    PatternSegmentIndex = segments.Count >= 2 ? segments.Count - 2 : -1;
                }

                PatternPreview = UseFolderPattern ? BuildPattern(path, PatternSegmentIndex) : path;
            }
            finally
            {
                _refreshing = false;
            }
        }

        /// <summary>The folders of a full file path after the drive/share root, outermost first (empty when it is not a full path).</summary>
        public static List<string> FolderSegmentsOf(string path)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '*', '?' }) >= 0 || !Path.IsPathFullyQualified(path))
            {
                return result;
            }

            var root = Path.GetPathRoot(path) ?? string.Empty;
            var rest = path[root.Length..].Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return rest.Take(Math.Max(0, rest.Length - 1)).ToList();
        }

        /// <summary>Replaces the folder at <paramref name="segmentIndex"/> (see <see cref="FolderSegmentsOf"/>) by "*". Index -1 / out of range returns the path unchanged.</summary>
        public static string BuildPattern(string path, int segmentIndex)
        {
            var folders = FolderSegmentsOf(path);
            if (segmentIndex < 0 || segmentIndex >= folders.Count)
            {
                return path;
            }

            var root = Path.GetPathRoot(path) ?? string.Empty;
            var rest = path[root.Length..].Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            rest[segmentIndex] = "*";
            return root + string.Join('\\', rest);
        }

        private void SetMessage(string keyOrText, bool error, bool literal = false)
        {
            Message = literal ? keyOrText : _localization.GetString(keyOrText);
            MessageIsError = error;
        }

        private static string? DefaultBrowse()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Add-in (*.dll)|*.dll|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }
    }
}
