using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RoslynPad.UI.Services;
using RoslynPad.UI.Utilities;
using RoslynPad.Utilities;

namespace RoslynPad.UI
{
    [DebuggerDisplay("{Name}:{IsFolder}")]
    public class DocumentViewModel : NotificationObject
    {
        private readonly IDisposable _documentFileWatcherDisposable;
        private readonly DocumentFileWatcher _documentFileWatcher;
        internal const string DefaultFileExtension = ".csx";
        internal const string AutoSaveSuffix = ".autosave";

        private ObservableCollection<DocumentViewModel> _children;
        private bool _isExpanded;
        private bool? _isAutoSaveOnly;
        private bool _isSearchMatch;
        private string _path;
        private string _name;

        private DocumentViewModel(string rootPath, DocumentFileWatcher documentFileWatcher)
        {
            _documentFileWatcher = documentFileWatcher;
            _documentFileWatcherDisposable = _documentFileWatcher.Subscribe(OnDocumentFileChanged);
            Path = rootPath;
            IsFolder = IOUtilities.IsDirectory(Path);
            Name = System.IO.Path.GetFileName(Path);
            IsAutoSave = Name.EndsWith(AutoSaveSuffix, StringComparison.OrdinalIgnoreCase);
            if (IsAutoSave)
            {
                Name = Name.Substring(0, Name.Length - AutoSaveSuffix.Length);
            }
            IsSearchMatch = true;
        }

        public string Path
        {
            get => _path;
            set
            {
                if (IsFolder)
                {
                    foreach (var child in Children)
                    {
                        child.Path = child.Path.Replace (_path, value);
                    }
                }
                SetProperty (ref _path, value);
            }
        }

        public bool IsFolder { get; }

        public string GetSavePath()
        {
            return IsAutoSave
                // ReSharper disable once AssignNullToNotNullAttribute
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), Name + DefaultFileExtension)
                : Path;
        }

        public string GetAutoSavePath()
        {
            return IsAutoSave ?
                Path
                // ReSharper disable once AssignNullToNotNullAttribute
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), GetAutoSaveName(Name));
        }

        public static string GetAutoSaveName(string name)
        {
            return name + AutoSaveSuffix + DefaultFileExtension;
        }

        public static DocumentViewModel CreateRoot(string rootPath, DocumentFileWatcher documentFileWatcher)
        {
            IOUtilities.PerformIO(() => Directory.CreateDirectory(rootPath));
            return new DocumentViewModel(rootPath, documentFileWatcher);
        }

        public static DocumentViewModel FromPath(string path, DocumentFileWatcher documentFileWatcher)
        {
            return new DocumentViewModel(path, documentFileWatcher);
        }

        public DocumentViewModel CreateNew(string documentName, DocumentFileWatcher documentFileWatcher)
        {
            if (!IsFolder) throw new InvalidOperationException("Parent must be a folder");

            var document = new DocumentViewModel(GetDocumentPathFromName(Path, documentName), documentFileWatcher);
            Children.Add(document);
            Children.Sort(SortPredicate);
            return document;
        }

        public static string GetDocumentPathFromName(string path, string name)
        {
            if (!name.EndsWith(DefaultFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                name += DefaultFileExtension;
            }

            return System.IO.Path.Combine(path, name);
        }

        public void DeleteAutoSave()
        {
            if (IsAutoSave)
            {
                IOUtilities.PerformIO(() => File.Delete(Path));
            }
            else
            {
                var autoSavePath = GetAutoSavePath();
                if (File.Exists(autoSavePath))
                {
                    IOUtilities.PerformIO(() => File.Delete(autoSavePath));
                }
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public string Name
        {
            get => _name;
            set
            {
                if (!IsFolder)
                    value = System.IO.Path.GetFileNameWithoutExtension (value);

                SetProperty (ref _name, value);
            }
        }

        public bool IsAutoSave { get; }

        public bool IsAutoSaveOnly
        {
            get
            {
                if (_isAutoSaveOnly == null)
                {
                    _isAutoSaveOnly = IsAutoSave &&
                                      // ReSharper disable once AssignNullToNotNullAttribute
                                      !File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), Name + DefaultFileExtension));
                }

                return _isAutoSaveOnly.Value;
            }
        }

        public ObservableCollection<DocumentViewModel> Children
        {
            get
            {
                if (IsFolder && _children == null)
                {
                    _children = ReadChildren();
                }

                return _children;
            }
        }

        public bool IsSearchMatch
        {
            get => _isSearchMatch;
            internal set => SetProperty(ref _isSearchMatch, value);
        }

        private ObservableCollection<DocumentViewModel> ReadChildren()
        {
            return new ObservableCollection<DocumentViewModel>(
                IOUtilities.EnumerateDirectories(Path)
                    .Concat(IOUtilities.EnumerateFiles(Path, "*" + DefaultFileExtension))
                    .Select(x => new DocumentViewModel(x, _documentFileWatcher))
                    .Where(x => !x.IsAutoSave)
                    .OrderBy(dd => !dd.IsFolder)
                    .ThenBy(OrderByName));
        }

        private static string OrderByName(DocumentViewModel x)
        {
            return Regex.Replace(x.Name, "[0-9]+", m => m.Value.PadLeft(100, '0'));
        }

        private static Func<IEnumerable<DocumentViewModel>, IOrderedEnumerable<DocumentViewModel>> SortPredicate =>
            d => d.OrderBy (dd => !dd.IsFolder).ThenBy (OrderByName);

        public void OnDocumentFileChanged (DocumentFileChanged value)
        {
            if (!IsFolder && value.Path != Path)
            {
                return;
            }
            if (IsFolder && value.Path != Path && System.IO.Path.GetDirectoryName(value.Path) != Path)
            {
                return;
            }

            switch (value.Type)
            {
            case DocumentFileChangeType.Created:
                OnDocumentCreated(value);
                break;
            case DocumentFileChangeType.Deleted:
                OnDocumentDeleted(value);
                break;
            case DocumentFileChangeType.Renamed:
                OnDocumentRenamed(value);
                break;
            }
        }

        private void OnDocumentRenamed (DocumentFileChanged value)
        {
            if (Path != value.Path)
                return; //Rename only applies to self
            Path = value.NewPath;
            Name = System.IO.Path.GetFileName(value.NewPath);
        }

        private void OnDocumentCreated (DocumentFileChanged value)
        {
            if (!IsFolder)
                return;

            if (Children.Any(d => d.Path == value.Path)) //if already added for some strange reason
                return;

            //We only add supported files
            if (System.IO.Path.GetExtension(value.Path) != ".csx" && !IOUtilities.IsDirectory(value.Path)) //TODO: get supported extensions
                return;

            Children.Add(new DocumentViewModel(value.Path, _documentFileWatcher));
            Children.Sort(SortPredicate);
        }

        private void OnDocumentDeleted (DocumentFileChanged value)
        {
            if (value.Path == Path)
            {
                //Since this document was removed it no longer needs to watch it's status
                _documentFileWatcherDisposable.Dispose();
                return;
            }
            var child = Children.SingleOrDefault(c => c.Path == value.Path);
            Children.Remove(child);
        }
    }
}