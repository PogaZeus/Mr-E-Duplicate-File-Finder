using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows; // For MessageBox, Window, MessageBoxButton, etc.
using System.Windows.Controls; // For ListView, MenuItem, ContextMenu

// DO NOT use: using System.Windows.Forms;

namespace DuplicateFinder
{
    public class FolderTreeNode
    {
        public string FolderName { get; set; }
        public string FolderPath { get; set; }
        public int ExactMatchCount { get; set; }
        public int CloseMatchCount { get; set; }
        public ObservableCollection<FolderTreeNode> Children { get; set; } = new ObservableCollection<FolderTreeNode>();
    }
    public partial class MainWindow : Window
    {
        public ObservableCollection<FolderSummary> FolderSummaries { get; set; }
            = new ObservableCollection<FolderSummary>();

        private List<FileDetail> _exactMatches = new List<FileDetail>();
        private List<FileDetail> _closeMatches = new List<FileDetail>();

        private int _totalFiles;
        private bool _isStopRequested;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly object _matchesLock = new object();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            UpdateStatus("Awaiting Folder Selection");
        }

        public class DuplicateFileNode
        {
            public string FileName { get; set; }
            public List<FileDetail> DuplicatePaths { get; set; } = new List<FileDetail>();
        }

        private void BuildResultsTree()
        {
            var rootItems = new List<DuplicateFileNode>();

            // Exact Matches
            var exactGroups = new Dictionary<string, List<FileDetail>>();
            lock (_matchesLock)
            {
                foreach (var f in _exactMatches)
                {
                    if (!exactGroups.ContainsKey(f.FileName))
                        exactGroups[f.FileName] = new List<FileDetail>();
                    exactGroups[f.FileName].Add(f);
                }
            }

            if (exactGroups.Count > 0)
            {
                // Header
                rootItems.Add(new DuplicateFileNode { FileName = "📁 Exact Matches" });
                // Files
                foreach (var group in exactGroups.OrderBy(g => g.Key))
                {
                    rootItems.Add(new DuplicateFileNode
                    {
                        FileName = group.Key,
                        DuplicatePaths = group.Value.OrderBy(f => f.FilePath).ToList()
                    });
                }
            }

            // Close Matches
            var closeGroups = new Dictionary<string, List<FileDetail>>();
            lock (_matchesLock)
            {
                foreach (var f in _closeMatches)
                {
                    if (!closeGroups.ContainsKey(f.FileName))
                        closeGroups[f.FileName] = new List<FileDetail>();
                    closeGroups[f.FileName].Add(f);
                }
            }

            if (closeGroups.Count > 0)
            {
                // Header
                rootItems.Add(new DuplicateFileNode { FileName = "📁 Close Matches" });
                // Files
                foreach (var group in closeGroups.OrderBy(g => g.Key))
                {
                    rootItems.Add(new DuplicateFileNode
                    {
                        FileName = group.Key,
                        DuplicatePaths = group.Value.OrderBy(f => f.FilePath).ToList()
                    });
                }
            }

            tvResults.ItemsSource = rootItems;
        }

        private int CountAccessibleFiles(string rootPath, CancellationToken token)
        {
            int count = 0;
            var directories = new Queue<string>();
            directories.Enqueue(rootPath);

            while (directories.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                string currentDir = directories.Dequeue();

                if (TryGetFiles(currentDir, out FileInfo[] files))
                {
                    count += files.Length;
                }

                if (TryGetDirectories(currentDir, out string[] subDirs))
                {
                    foreach (var dir in subDirs)
                        directories.Enqueue(dir);
                }
            }

            return count;
        }

        private async void SelectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                txtFolderPath.Text = dialog.SelectedPath;

                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource = new CancellationTokenSource();
                _isStopRequested = false; // Reset

                // Clear previous results
                lock (_matchesLock)
                {
                    _exactMatches.Clear();
                    _closeMatches.Clear();
                }
                lvExactMatches.Items.Clear();
                lvCloseMatches.Items.Clear();
                FolderSummaries.Clear();

                // Show UI
                UpdateStatus("Scanning...");
                progressBar.Visibility = Visibility.Visible;
                txtProgress.Visibility = Visibility.Visible;
                btnStop.Visibility = Visibility.Visible;
                btnCancel.Visibility = Visibility.Visible;
                progressBar.IsIndeterminate = true;
                txtProgress.Text = "Counting Files...";

                await ScanForDuplicatesAsync(dialog.SelectedPath);
            }
        }
        private async Task ScanForDuplicatesAsync(string folderPath)
        {
            var token = _cancellationTokenSource.Token;

            try
            {
                await Task.Run(() => PerformScanWithPartialResults(folderPath, token), token);

                UpdateStatus("Scan Completed");
                RefreshUI(folderPath);
            }
            catch (OperationCanceledException)
            {
                if (_isStopRequested)
                {
                    UpdateStatus("Scan stopped. Showing partial results.");
                    RefreshUI(folderPath); // ← Show what we have
                }
                else
                {
                    UpdateStatus("Scan canceled.");
                    // Do NOT refresh — results are discarded
                    // Clear lists (optional, but clean)
                    lock (_matchesLock)
                    {
                        _exactMatches.Clear();
                        _closeMatches.Clear();
                    }
                    lvExactMatches.Items.Clear();
                    lvCloseMatches.Items.Clear();
                    FolderSummaries.Clear();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Scan failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus("Scan Failed");
                // Clear on error
                lock (_matchesLock)
                {
                    _exactMatches.Clear();
                    _closeMatches.Clear();
                }
                lvExactMatches.Items.Clear();
                lvCloseMatches.Items.Clear();
                FolderSummaries.Clear();
            }
            finally
            {
                progressBar.Visibility = Visibility.Collapsed;
                txtProgress.Visibility = Visibility.Collapsed;
                btnStop.Visibility = Visibility.Collapsed;
                btnCancel.Visibility = Visibility.Collapsed;
            }
        }

        private void PerformScanWithPartialResults(string folderPath, CancellationToken token)
        {
            var files = GetAccessibleFiles(folderPath).ToList();
            _totalFiles = files.Count;
            var processed = 0;

            var groupedByName = files.GroupBy(f => f.Name);

            foreach (var group in groupedByName)
            {
                token.ThrowIfCancellationRequested();

                if (group.Count() > 1)
                {
                    var fileList = group.ToList();
                    var newExact = new List<FileDetail>();
                    var newClose = new List<FileDetail>();

                    for (int i = 0; i < fileList.Count; i++)
                    {
                        for (int j = i + 1; j < fileList.Count; j++)
                        {
                            token.ThrowIfCancellationRequested();

                            var fileA = fileList[i];
                            var fileB = fileList[j];

                            bool sameSize = fileA.Length == fileB.Length;
                            bool sameDate = fileA.CreationTime == fileB.CreationTime;

                            var detailA = new FileDetail(fileA);
                            var detailB = new FileDetail(fileB);

                            if (sameSize && sameDate)
                            {
                                if (!newExact.Any(f => f.FilePath == detailA.FilePath))
                                    newExact.Add(detailA);
                                if (!newExact.Any(f => f.FilePath == detailB.FilePath))
                                    newExact.Add(detailB);
                            }
                            else
                            {
                                if (!newClose.Any(f => f.FilePath == detailA.FilePath))
                                    newClose.Add(detailA);
                                if (!newClose.Any(f => f.FilePath == detailB.FilePath))
                                    newClose.Add(detailB);
                            }
                        }
                    }

                    // Add to global lists (thread-safe)
                    lock (_matchesLock)
                    {
                        _exactMatches.AddRange(newExact);
                        _closeMatches.AddRange(newClose);
                    }
                }

                processed += group.Count();
                UpdateProgress(processed, _totalFiles);
            }
        }

        private void UpdateProgress(int current, int total)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (progressBar.IsIndeterminate) progressBar.IsIndeterminate = false;
                    progressBar.Maximum = total;
                    progressBar.Value = Math.Min(current, total);
                    txtProgress.Text = $"{current:N0} / {total:N0} files";
                });
            }
            catch { /* Ignore if UI is gone */ }
        }

        private void RefreshUI(string folderPath)
        {
            Dispatcher.Invoke(() =>
            {
                // Update list views (Exact & Close tabs)
                lvExactMatches.Items.Clear();
                lvCloseMatches.Items.Clear();
                lock (_matchesLock)
                {
                    foreach (var item in _exactMatches) lvExactMatches.Items.Add(item);
                    foreach (var item in _closeMatches) lvCloseMatches.Items.Add(item);
                }

                BuildResultsTree();

                try
                {
                    var root = BuildFolderTree(folderPath);
                    tvFolders.ItemsSource = new[] { root }; // Wrap root in array for TreeView
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading folder tree: {ex.Message}", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            _isStopRequested = true; // Will return partial results
            _cancellationTokenSource?.Cancel();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            _isStopRequested = false; // Ensure Cancel mode, no results returned
            _cancellationTokenSource?.Cancel();
        }

        private void UpdateStatus(string message)
        {
            txtStatus.Text = message;
        }
        private static IEnumerable<FileInfo> GetAccessibleFiles(string rootPath)
        {
            var directories = new Queue<string>();
            directories.Enqueue(rootPath);

            while (directories.Count > 0)
            {
                string currentDir = directories.Dequeue();

                // Get files in current directory
                if (TryGetFiles(currentDir, out FileInfo[] files))
                {
                    foreach (var file in files)
                    {
                        yield return file;
                    }
                }

                // Get subdirectories
                if (TryGetDirectories(currentDir, out string[] subDirs))
                {
                    foreach (var dir in subDirs)
                    {
                        directories.Enqueue(dir);
                    }
                }
            }
        }

        // Helper: Safely get files from a directory
        private static bool TryGetFiles(string path, out FileInfo[] files)
        {
            try
            {
                var dir = new DirectoryInfo(path);
                files = dir.GetFiles();
                return true;
            }
            catch
            {
                files = Array.Empty<FileInfo>();
                return false;
            }
        }

        // Helper: Safely get subdirectory names
        private static bool TryGetDirectories(string path, out string[] directories)
        {
            try
            {
                var dir = new DirectoryInfo(path);
                directories = dir.GetDirectories().Select(d => d.FullName).ToArray();
                return true;
            }
            catch
            {
                directories = Array.Empty<string>();
                return false;
            }
        }

        // ============== CONTEXT MENU HANDLERS ==============
        private FileDetail GetSelectedFile(object sender)
        {
            // These are now UNAMBIGUOUS because we removed WinForms "using"
            if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu)
            {
                if (contextMenu.PlacementTarget is ListView listView)
                {
                    return listView.SelectedItem as FileDetail;
                }
            }
            return null;
        }

        private void ContextMenu_OpenFile_Click(object sender, RoutedEventArgs e)
        {
            // Try TreeView first, then ListView
            var file = GetSelectedFileFromTreeView(sender) ?? GetSelectedFile(sender);
            if (file == null || !File.Exists(file.FilePath))
            {
                MessageBox.Show("File not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = file.FilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ContextMenu_OpenFilePath_Click(object sender, RoutedEventArgs e)
        {
            var file = GetSelectedFileFromTreeView(sender) ?? GetSelectedFile(sender);
            if (file == null || !File.Exists(file.FilePath))
            {
                MessageBox.Show("File not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Process.Start("explorer.exe", $"/select,\"{file.FilePath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open file path:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ContextMenu_DeleteFile_Click(object sender, RoutedEventArgs e)
        {
            var file = GetSelectedFileFromTreeView(sender) ?? GetSelectedFile(sender);
            if (file == null || !File.Exists(file.FilePath))
            {
                MessageBox.Show("File not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Are you sure you want to delete this file?\n\n{file.FilePath}",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    File.Delete(file.FilePath);

                    // Remove from global lists
                    _exactMatches.RemoveAll(f => f.FilePath == file.FilePath);
                    _closeMatches.RemoveAll(f => f.FilePath == file.FilePath);

                    // Remove from ListViews
                    lvExactMatches.Items.Remove(file);
                    lvCloseMatches.Items.Remove(file);

                    // Refresh Results tab
                    BuildResultsTree();

                    MessageBox.Show("File deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    UpdateStatus("File deleted. Scan results updated.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to delete file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public class FileDetail
        {
            public string FileName { get; set; }
            public string FilePath { get; set; }
            public long SizeKB { get; set; }
            public DateTime CreatedDate { get; set; }

            public FileDetail(FileInfo file)
            {
                FileName = file.Name;
                FilePath = file.FullName;
                SizeKB = file.Length / 1024;
                CreatedDate = file.CreationTime;
            }

            // Helper property for display in Close Matches
            public string SizeAndDate => $"Size: {SizeKB} KB | Created: {CreatedDate:yyyy-MM-dd HH:mm}";
        }

        public class FolderSummary
        {
            public string FolderName { get; set; }
            public string FolderPath { get; set; }
            public int ExactMatchCount { get; set; }
            public int CloseMatchCount { get; set; }
        }

        private FileDetail GetSelectedFileFromTreeView(object sender)
        {
            if (sender is MenuItem menuItem &&
                menuItem.Parent is ContextMenu contextMenu &&
                contextMenu.PlacementTarget is TreeViewItem treeViewItem)
            {
                // The DataContext of a TreeViewItem is either:
                // - DuplicateFileNode (for file name headers)
                // - FileDetail (for file path items)
                var data = treeViewItem.DataContext;
                if (data is FileDetail fileDetail)
                {
                    return fileDetail;
                }
            }
            return null;
        }

        private FolderTreeNode BuildFolderTree(string rootPath)
        {
            var rootDir = new DirectoryInfo(rootPath);
            var root = new FolderTreeNode
            {
                FolderName = "[Selected Folder]",
                FolderPath = rootPath,
                ExactMatchCount = GetExactMatchCount(rootPath),
                CloseMatchCount = GetCloseMatchCount(rootPath)
            };

            AddChildren(rootDir, root);
            return root;
        }

        private void AddChildren(DirectoryInfo parentDir, FolderTreeNode parent)
        {
            try
            {
                foreach (var dir in parentDir.GetDirectories())
                {
                    var node = new FolderTreeNode
                    {
                        FolderName = dir.Name,
                        FolderPath = dir.FullName,
                        ExactMatchCount = GetExactMatchCount(dir.FullName),
                        CloseMatchCount = GetCloseMatchCount(dir.FullName)
                    };

                    parent.Children.Add(node);
                    AddChildren(dir, node); // Recurse
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible folders — don't add children
            }
            catch (DirectoryNotFoundException)
            {
                // Skip deleted folders
            }
            catch (IOException)
            {
                // Skip locked/long-path folders
            }
        }

        private int GetExactMatchCount(string folderPath)
        {
            lock (_matchesLock)
            {
                return _exactMatches.Count(f => f.FilePath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase));
            }
        }

        private int GetCloseMatchCount(string folderPath)
        {
            lock (_matchesLock)
            {
                return _closeMatches.Count(f => f.FilePath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    }