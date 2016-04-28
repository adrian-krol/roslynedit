using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.ApplicationInsights;
using RoslynPad.Host;
using RoslynPad.Roslyn;
using RoslynPad.Utilities;

namespace RoslynPad
{
    internal sealed class MainViewModel : NotificationObject
    {
        private static readonly Version _currentVersion = new Version(0, 6);

        private const string ApplicationInsightsInstrumentationKey = "86551688-26d9-4124-8376-3f7ddcf84b8e";
        public const string NuGetPathVariableName = "$NuGet";

        private readonly Lazy<TelemetryClient> _client;

        private OpenDocumentViewModel _currentOpenDocument;
        private Exception _lastError;
        private bool _hasUpdate;

        public DocumentViewModel DocumentRoot { get; }
        public INuGetProvider NuGetProvider { get; }
        public RoslynHost RoslynHost { get; }

        public MainViewModel()
        {
            NuGet = new NuGetViewModel();
            NuGetProvider = new NuGetProviderImpl(NuGet.GlobalPackageFolder, NuGetPathVariableName);
            RoslynHost = new RoslynHost(NuGetProvider);
            ChildProcessManager = new ChildProcessManager();

            NewDocumentCommand = new DelegateCommand((Action)CreateNewDocument);
            CloseCurrentDocumentCommand = new DelegateCommand(CloseCurrentDocument);
            ClearErrorCommand = new DelegateCommand(() => LastError = null);

            DocumentRoot = CreateDocumentRoot();
            Documents = DocumentRoot.Children;
            OpenDocuments = new ObservableCollection<OpenDocumentViewModel>(LoadAutoSaves(DocumentRoot.Path));
            OpenDocuments.CollectionChanged += (sender, args) => OnPropertyChanged(nameof(HasNoOpenDocuments));
            if (HasNoOpenDocuments)
            {
                CreateNewDocument();
            }
            else
            {
                CurrentOpenDocument = OpenDocuments[0];
            }

            _client = new Lazy<TelemetryClient>(() => new TelemetryClient { InstrumentationKey = ApplicationInsightsInstrumentationKey });

            Application.Current.DispatcherUnhandledException += (o, e) => OnUnhandledDispatcherException(e);
            AppDomain.CurrentDomain.UnhandledException += (o, e) => OnUnhandledException((Exception)e.ExceptionObject, flushSync: true);
            TaskScheduler.UnobservedTaskException += (o, e) => OnUnhandledException(e.Exception);

            if (HasCachedUpdate())
            {
                HasUpdate = true;
            }
            else
            {
                Task.Run(CheckForUpdates);
            }
        }

        public IEnumerable<OpenDocumentViewModel> LoadAutoSaves(string root)
        {
            return Directory.EnumerateFiles(root, DocumentViewModel.GetAutoSaveName("*"), SearchOption.AllDirectories)
                .Select(x => new OpenDocumentViewModel(this, DocumentViewModel.CreateAutoSave(this, x)));
        }

        public bool HasUpdate
        {
            get { return _hasUpdate; }
            private set { SetProperty(ref _hasUpdate, value); }
        }

        private static bool HasCachedUpdate()
        {
            Version latestVersion;
            return Version.TryParse(Properties.Settings.Default.LatestVersion, out latestVersion) &&
                   latestVersion > _currentVersion;
        }

        private async Task CheckForUpdates()
        {
            string latestVersionString;
            using (var client = new HttpClient())
            {
                try
                {
                    latestVersionString = await client.GetStringAsync("https://roslynpad.net/latest").ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
            }
            Version latestVersion;
            if (Version.TryParse(latestVersionString, out latestVersion))
            {
                if (latestVersion > _currentVersion)
                {
                    HasUpdate = true;
                }
                Properties.Settings.Default.LatestVersion = latestVersionString;
                Properties.Settings.Default.Save();
            }
        }

        private DocumentViewModel CreateDocumentRoot()
        {
            var root = DocumentViewModel.CreateRoot(this);
            if (!Directory.Exists(Path.Combine(root.Path, "Samples")))
            {
                // ReSharper disable once PossibleNullReferenceException
                using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/RoslynPad;component/Resources/Samples.zip")).Stream)
                using (var archive = new ZipArchive(stream))
                {
                    archive.ExtractToDirectory(root.Path);
                }
            }
            return root;
        }

        public NuGetViewModel NuGet { get; }

        public ObservableCollection<OpenDocumentViewModel> OpenDocuments { get; }

        public OpenDocumentViewModel CurrentOpenDocument
        {
            get { return _currentOpenDocument; }
            set { SetProperty(ref _currentOpenDocument, value); }
        }

        public ObservableCollection<DocumentViewModel> Documents { get; }

        public DelegateCommand NewDocumentCommand { get; }

        public DelegateCommand CloseCurrentDocumentCommand { get; }

        public void OpenDocument(DocumentViewModel document)
        {
            var openDocument = OpenDocuments.FirstOrDefault(x => x.Document == document);
            if (openDocument == null)
            {
                openDocument = new OpenDocumentViewModel(this, document);
                OpenDocuments.Add(openDocument);
            }
            CurrentOpenDocument = openDocument;
        }

        public void CreateNewDocument()
        {
            var openDocument = new OpenDocumentViewModel(this, null);
            OpenDocuments.Add(openDocument);
            CurrentOpenDocument = openDocument;
        }

        public async Task<bool> CloseDocument(OpenDocumentViewModel document)
        {
            try
            {
                await document.Save(showDontSave: true, promptSave: true).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            if (document.Document?.IsAutoSave == true)
            {
                File.Delete(document.Document.Path);
            }
            RoslynHost.CloseDocument(document.DocumentId);
            OpenDocuments.Remove(document);
            document.Close();
            return true;
        }

        public async Task AutoSaveOpenDocuments()
        {
            foreach (var document in OpenDocuments)
            {
                await document.AutoSave().ConfigureAwait(false);
            }
        }

        private async Task CloseCurrentDocument()
        {
            if (CurrentOpenDocument != null)
            {
                await CloseDocument(CurrentOpenDocument).ConfigureAwait(false);
            }
        }

        private void OnUnhandledException(Exception exception, bool flushSync = false)
        {
            TrackException(exception, flushSync);
        }

        private void OnUnhandledDispatcherException(DispatcherUnhandledExceptionEventArgs args)
        {
            TrackException(args.Exception);
            LastError = args.Exception;
            args.Handled = true;
        }

        public async Task OnExit()
        {
            await AutoSaveOpenDocuments().ConfigureAwait(false);

            if (_client.IsValueCreated)
            {
                _client.Value.Flush();
            }
        }

        private void TrackException(Exception exception, bool flushSync = false)
        {
            // ReSharper disable once RedundantLogicalConditionalExpressionOperand
            if (SendErrors && ApplicationInsightsInstrumentationKey != null)
            {
                _client.Value.TrackException(exception);
                if (flushSync)
                {
                    _client.Value.Flush();
                }
                // TODO: check why this freezes the UI
                //else
                //{
                //    Task.Run(() => _client.Value.Flush());
                //}
            }
        }

        public Exception LastError
        {
            get { return _lastError; }
            private set
            {
                SetProperty(ref _lastError, value);
                OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => LastError != null;

        public DelegateCommand ClearErrorCommand { get; }

        public bool SendErrors
        {
            get { return Properties.Settings.Default.SendErrors; }
            set
            {
                Properties.Settings.Default.SendErrors = value;
                Properties.Settings.Default.Save();
                OnPropertyChanged(nameof(SendErrors));
            }
        }

        public ChildProcessManager ChildProcessManager { get; }

        public bool HasNoOpenDocuments => OpenDocuments.Count == 0;

        public DocumentViewModel AddDocument(string documentName)
        {
            return DocumentRoot.CreateNew(documentName);
        }

        [Serializable]
        private class NuGetProviderImpl : INuGetProvider
        {
            public NuGetProviderImpl(string pathToRepository, string pathVariableName)
            {
                PathToRepository = pathToRepository;
                PathVariableName = pathVariableName;
            }

            public string PathToRepository { get; }
            public string PathVariableName { get; }
        }
    }
}