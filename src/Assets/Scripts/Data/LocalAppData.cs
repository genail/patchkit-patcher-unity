﻿using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PatchKit.Unity.Patcher.Cancellation;
using PatchKit.Unity.Patcher.Diff;
using PatchKit.Unity.Patcher.Log;
using PatchKit.Unity.Patcher.Statistics;
using PatchKit.Unity.Patcher.Zip;
using UnityEngine;

namespace PatchKit.Unity.Patcher.Data
{
    internal class LocalAppData : IDebugLogger
    {
        private LocalFileSystem _fileSystem;

        private const string MetaDataFileName = "patcher_cache.json";

        private readonly string _metaDataFilePath;

        private readonly LocalMetaData _metaData;

        private readonly Unzipper _unzipper;

        private readonly Librsync _librsync;

        private bool? _isWritable;

        private readonly string _path;

        public LocalAppData(string path)
        {
            this.Log(string.Format("Initializing local application data in {0}", path));

            _path = path;

            _fileSystem = new LocalFileSystem(_path);

            _metaDataFilePath = Path.Combine(_path, MetaDataFileName);

            _metaData = new LocalMetaData();
            if (File.Exists(_metaDataFilePath))
            {
                _metaData.Deserialize(_metaDataFilePath);
            }

            _unzipper = new Unzipper();
            _librsync = new Librsync();
        }

        private void AddFile(string fileName, string sourceFilePath, int versionId)
        {
            Debug.Log(string.Format("Adding file {0} from source {1} as version {2}", fileName, sourceFilePath,
                versionId));

            string filePath = Path.Combine(_path, fileName);
            string fileDirectoryPath = Path.GetDirectoryName(filePath);

            if (fileDirectoryPath != null && !Directory.Exists(fileDirectoryPath))
            {
                Directory.CreateDirectory(fileDirectoryPath);
            }

            File.Copy(sourceFilePath, filePath, true);
            _metaData.SetFileVersionId(fileName, versionId);
            _metaData.Serialize(_metaDataFilePath);
        }

        private void PatchFile(string fileName, string diffFilePath, int versionId)
        {
            Debug.Log(string.Format("Patching file {0} with diff {1} to version {2}", fileName, diffFilePath, versionId));

            string filePath = Path.Combine(_path, fileName);

            _librsync.Patch(filePath, diffFilePath);

            _metaData.SetFileVersionId(fileName, versionId);
            _metaData.Serialize(_metaDataFilePath);
        }

        private bool CheckFile(string fileName, int versionId, string hash = null)
        {
            Debug.Log(string.Format("Checking file {0} of version {1}", fileName, versionId));

            var fileVersionId = _metaData.GetFileVersionId(fileName);

            if (fileVersionId == null || fileVersionId.Value != versionId)
            {
                Debug.Log(string.Format("File {0} has invaild version {1}", fileName,
                    fileVersionId == null ? "<none>" : fileVersionId.Value.ToString()));

                return false;
            }

            string filePath = Path.Combine(_path, fileName);

            if (!File.Exists(filePath))
            {
                Debug.Log(string.Format("File doesn't exists {0}", fileName));

                return false;
            }

            if (hash != null)
            {
                // TODO: Check hash
            }

            return true;
        }

        private void DeleteFile(string fileName)
        {
            Debug.Log(string.Format("Trying to delete file {0}", fileName));

            string filePath = Path.Combine(_path, fileName);

            if (File.Exists(filePath))
            {
                Debug.Log(string.Format("Deleting file {0}", fileName));
                File.Delete(filePath);
            }
            else
            {
                Debug.Log(string.Format("File already doesn't exist {0}", fileName));
            }

            _metaData.ClearFileVersionId(fileName);
            _metaData.Serialize(_metaDataFilePath);
        }

        private void ProcessContent(string sourcePath, JObject contentSummary, int versionId,
            StepProgressReporter progressReporter, CancellationToken cancellationToken)
        {
            Debug.Log(string.Format("Processing content from {0} of version {1}", sourcePath, versionId));

            var fileNames = contentSummary.Value<JArray>("files").Values<JObject>().Select(o => o.Value<string>("path")).ToArray();
            progressReporter.TotalSteps = fileNames.Length;

            foreach (string fileName in fileNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sourceFilePath = Path.Combine(sourcePath, fileName);

                AddFile(fileName, sourceFilePath, versionId);

                progressReporter.Step();
            }
        }

        private void ProcessDiffRemovedFiles(JObject diffSummary, StepProgressReporter progressReporter,
            CancellationToken cancellationToken)
        {
            Debug.Log("Processing diff removed files.");

            var removedFiles = diffSummary.Value<JArray>("removed_files").Values<string>()
                .ToArray();

            progressReporter.TotalSteps = removedFiles.Length;

            // 1. Delete files.
            foreach (var fileName in removedFiles.Where(s => !s.EndsWith("/")))
            {
                cancellationToken.ThrowIfCancellationRequested();

                DeleteFile(fileName);

                progressReporter.Step();
            }

            // 2. Because files has been deleted now we can try to delete directories.
            // Note that directories that still contains files will not be removed.
            foreach (var dirName in removedFiles.Where(s => s.EndsWith("/")))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_fileSystem.DirectoryExists(dirName) && !_fileSystem.IsDirectoryEmpty(dirName))
                {
                    _fileSystem.DeleteDirectory(dirName);
                }

                progressReporter.Step();
            }

            progressReporter.Finish();
        }

        private void ProcessDiffAddedFiles(string sourcePath, JObject diffSummary, int versionId,
            StepProgressReporter progressReporter, CancellationToken cancellationToken)
        {
            Debug.Log(string.Format("Processing diff added files from {0} of version {1}.", sourcePath, versionId));

            var addedFiles = diffSummary.Value<JArray>("added_files").Values<string>()
                .ToArray();

            progressReporter.TotalSteps = addedFiles.Length;

            // 1. Add directories.
            foreach (var dirName in addedFiles.Where(s => s.EndsWith("/")))
            {
                cancellationToken.ThrowIfCancellationRequested();

                _fileSystem.CreateDirectory(dirName);

                progressReporter.Step();
            }

            // 2. Add files.
            foreach (var fileName in addedFiles.Where(s => !s.EndsWith("/")))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sourceFilePath = Path.Combine(sourcePath, fileName);

                AddFile(fileName, sourceFilePath, versionId);

                progressReporter.Step();
            }

            progressReporter.Finish();
        }

        private void ProcessDiffModifiedFiles(string sourcePath, JObject diffSummary, int versionId,
            StepProgressReporter progressReporter, CancellationToken cancellationToken)
        {
            Debug.Log(string.Format("Processing diff modified files from {0} of version {1}.", sourcePath, versionId));

            var modifiedFiles = diffSummary.Value<JArray>("modified_files").Values<string>()
                .ToArray();

            progressReporter.TotalSteps = modifiedFiles.Length;

            foreach (var fileName in modifiedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string filePath = Path.Combine(_path, fileName);

                // HACK: Workaround for directories included in diff summary.
                if (Directory.Exists(filePath))
                {
                    progressReporter.Step();
                    continue;
                }

                string diffFilePath = Path.Combine(sourcePath, fileName);

                PatchFile(fileName, diffFilePath, versionId);

                progressReporter.Step();
            }

            progressReporter.Finish();
        }

        private void ProcessDiff(string sourcePath, JObject diffSummary, int versionId,
            ComplexProgressReporter progressReporter, CancellationToken cancellationToken)
        {
            var processDiffRemovedFilesProgressReporter = new StepProgressReporter();
            var processDiffAddedFilesProgressReporter = new StepProgressReporter();
            var processDiffModifiedFilesProgressReporter = new StepProgressReporter();

            progressReporter.AddChild(processDiffRemovedFilesProgressReporter, 0.5);
            progressReporter.AddChild(processDiffAddedFilesProgressReporter, 1.0);
            progressReporter.AddChild(processDiffModifiedFilesProgressReporter, 2.5);

            ProcessDiffRemovedFiles(diffSummary, processDiffRemovedFilesProgressReporter, cancellationToken);
            ProcessDiffAddedFiles(sourcePath, diffSummary, versionId, processDiffAddedFilesProgressReporter,
                cancellationToken);
            ProcessDiffModifiedFiles(sourcePath, diffSummary, versionId, processDiffModifiedFilesProgressReporter,
                cancellationToken);
        }

        /// <summary>
        /// Uninstalls the application.
        /// </summary>
        public void Uninstall()
        {
            Debug.Log("Uninstalling application.");

            foreach (var fileName in _metaData.GetFileNames())
            {
                DeleteFile(fileName);
            }
        }

        /// <summary>
        /// Installs the application with content.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if application directory is not writable.</exception>
        /// <exception cref="InvalidOperationException">Thrown if application is already installed.</exception>
        public void Install(string contentPackagePath, JObject contentSummary, int versionId,
            ComplexProgressReporter progressReporter, CancellationToken cancellationToken)
        {
            Debug.Log(string.Format("Installing application with content package from {0} of version {1}.",
                contentPackagePath, versionId));

            if (!IsWritable())
            {
                throw new InvalidOperationException("Application directory is not writable.");
            }

            if (IsInstalled())
            {
                throw new InvalidOperationException("Application is already installed.");
            }

            var unzipPackageProgressReporter = new CustomProgressReporter<UnzipperProgress>();
            var processContentProgressReporter = new StepProgressReporter();

            progressReporter.AddChild(unzipPackageProgressReporter, 1.5);
            progressReporter.AddChild(processContentProgressReporter, 0.5);

            using (var temporaryDirectory = TemporaryDirectory.CreateDefault())
            {
                _unzipper.Unzip(contentPackagePath, temporaryDirectory.Path, unzipPackageProgressReporter,
                    cancellationToken);

                ProcessContent(temporaryDirectory.Path, contentSummary, versionId, processContentProgressReporter,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Patches the application with diff. 
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if application directory is not writable.</exception>
        /// <exception cref="InvalidOperationException">Thrown if application is not installed.</exception>
        /// <exception cref="InvalidOperationException">Thrown if application cannot be patched with specified diff version.</exception>
        /// <exception cref="InvalidOperationException">Thrown if application data is not consistent.</exception>
        public void Patch(string diffPackagePath, JObject diffSummary, JObject previousContentSummary, int versionId,
            ComplexProgressReporter progressReporter, CancellationToken cancellationToken)
        {
            Debug.Log(string.Format("Patching application with diff package from {0} of version {1}.",
                diffPackagePath, versionId));

            if (!IsWritable())
            {
                throw new InvalidOperationException("Application directory is not writable.");
            }

            if (!IsInstalled())
            {
                throw new InvalidOperationException("Application is not installed.");
            }

            // ReSharper disable once PossibleInvalidOperationException
            if (GetVersionId().Value + 1 != versionId)
            {
                throw new InvalidOperationException("Application cannot be patched with specified diff version.");
            }

            if (!CheckDataConsistency(previousContentSummary, versionId - 1))
            {
                throw new InvalidOperationException("Application data is not consistent.");
            }

            var unzipPackageProgressReporter = new CustomProgressReporter<UnzipperProgress>();
            var processDiffProgressReporter = new ComplexProgressReporter();

            progressReporter.AddChild(unzipPackageProgressReporter, 1.5);
            progressReporter.AddChild(processDiffProgressReporter, 2.5);

            using (var temporaryDirectory = TemporaryDirectory.CreateDefault())
            {
                _unzipper.Unzip(diffPackagePath, temporaryDirectory.Path, unzipPackageProgressReporter,
                    cancellationToken);

                ProcessDiff(temporaryDirectory.Path, diffSummary, versionId, processDiffProgressReporter,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Checks the data consistency.
        /// </summary>
        public bool CheckDataConsistency(JObject contentSummary, int versionId)
        {
            var localVersionId = GetVersionId();

            if (localVersionId == null || localVersionId.Value != versionId)
            {
                return false;
            }

            foreach (var fileName in contentSummary.Value<JArray>("files").Values<JObject>().Select(o => o.Value<string>("path")))
            {
                if (!CheckFile(fileName, versionId))
                {
                    Debug.Log(string.Format("Application data is not consistent because of file {0}", fileName));
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Determines whether application is installed.
        /// </summary>
        public bool IsInstalled()
        {
            return GetVersionId() != null;
        }

        /// <summary>
        /// Gets version id of installed application. If application is not installed, then <c>null</c> is returned.
        /// </summary>
        public int? GetVersionId()
        {
            int? version = null;

            foreach (var fileName in _metaData.GetFileNames())
            {
                int? fileVersion = _metaData.GetFileVersionId(fileName);

                if (!fileVersion.HasValue)
                {
                    continue;
                }

                if (version == null)
                {
                    version = fileVersion.Value;
                }
                else if (version.Value != fileVersion.Value)
                {
                    return null;
                }
            }

            return version;
        }

        /// <summary>
        /// Determines whether application directory is writable.
        /// </summary>
        public bool IsWritable()
        {
            if (_isWritable == null)
            {
                _isWritable = false;

                try
                {
                    string permissionsCheckFilePath = Path.Combine(_path, ".permissions_check");

                    if (!Directory.Exists(_path))
                    {
                        Directory.CreateDirectory(_path);
                    }

                    using (var fs = new FileStream(permissionsCheckFilePath, FileMode.CreateNew, FileAccess.Write))
                    {
                        fs.WriteByte(0xff);
                    }

                    if (File.Exists(permissionsCheckFilePath))
                    {
                        File.Delete(permissionsCheckFilePath);
                        _isWritable = true;
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    Debug.LogWarning("Directory is not writable.");
                }
            }

            return _isWritable.Value;
        }
    }
}