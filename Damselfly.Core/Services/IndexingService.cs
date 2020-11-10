﻿using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Microsoft.EntityFrameworkCore;
using Damselfly.Core.Models;
using Damselfly.Core.Utils;
using MetadataExtractor.Formats.Iptc;
using System.Threading;
using MetadataExtractor.Formats.Jpeg;
using EFCore.BulkExtensions;

namespace Damselfly.Core.Services
{
    /// <summary>
    /// Core indexing service, which is responsible for scanning the folders on
    /// disk for images, and to ingest them into the DB with all their extracted
    /// metadata, such as size, last modified date, etc., etc.
    /// </summary>
    public class IndexingService
    {
        // Some caching to avoid repeatedly reading tags, cameras and lenses
        // from the DB.
        private IDictionary<string, Models.Tag> _tagCache;
        private IDictionary<string, Camera> _cameraCache;
        private IDictionary<string, Lens> _lensCache;
        private IDictionary<string, FileSystemWatcher> _watchers = new Dictionary<string, FileSystemWatcher>( StringComparer.OrdinalIgnoreCase );

        public static string RootFolder { get; set; }
        public static IndexingService Instance { get; private set; }
        public static bool EnableIndexing { get; set; } = true;
        public static bool EnableThumbnailGeneration { get; set; } = true;

        public IndexingService()
        {
            Instance = this;
        }

        public event Action OnFoldersChanged;

        private void NotifyFolderChanged()
        {
            Logging.LogVerbose($"Folders changed.");

            // TODO - invoke back on dispatcher thread....
            OnFoldersChanged?.Invoke();
        }

        public IEnumerable<Models.Tag> CachedTags {  get { return _tagCache.Values.ToList();  } }

        /// <summary>
        /// Read the metadata, and handle any exceptions.
        /// </summary>
        /// <param name="imagePath"></param>
        /// <returns>Metadata, or Null if there was an error</returns>
        private IReadOnlyList<MetadataExtractor.Directory> SafeReadImageMetadata( string imagePath )
        {
            IReadOnlyList<MetadataExtractor.Directory> metadata = null;

            try
            {
                metadata = ImageMetadataReader.ReadMetadata(imagePath);
            }
            catch( ImageProcessingException ex )
            {
                Logging.Log("Metadata read for image {0}: {1}", imagePath, ex.Message);

            }
            catch (IOException ex)
            {
                Logging.Log("File error reading metadata for {0}: {1}", imagePath, ex.Message);

            }

            return metadata;
        }


        /// <summary>
        /// Scans an image file on disk for its metadata, using the MetaDataExtractor
        /// library. The image object is populated with the metadata, and the IPTC
        /// keywords are returned back to the caller for processing and ingestion
        /// into the DB.
        /// </summary>
        /// <param name="image">Image object, which will be updated with metadata</param>
        /// <param name="keywords">Array of keyword tags in the image EXIF data</param>
        private void GetImageMetaData(ref ImageMetaData imgMetaData, out string[] keywords)
        {
            var image = imgMetaData.Image;
            keywords = new string[0];

            try
            {
                var watch = new Stopwatch("ReadMetaData");

                IReadOnlyList<MetadataExtractor.Directory> metadata = SafeReadImageMetadata( image.FullPath );

                watch.Stop();

                // Update the timestamp
                imgMetaData.LastUpdated = DateTime.UtcNow;

                if (metadata != null)
                {
                    var jpegDirectory = metadata.OfType<JpegDirectory>().FirstOrDefault();

                    if (jpegDirectory != null)
                    {
                        imgMetaData.Width = jpegDirectory.SafeGetExifInt(JpegDirectory.TagImageWidth);
                        imgMetaData.Width = jpegDirectory.SafeGetExifInt(JpegDirectory.TagImageHeight);
                    }

                    var subIfdDirectory = metadata.OfType<ExifSubIfdDirectory>().FirstOrDefault();

                    if (subIfdDirectory != null)
                    {
                        var desc = subIfdDirectory.SafeExifGetString(ExifDirectoryBase.TagImageDescription);

                        imgMetaData.Description = FilteredDescription( desc );

                        imgMetaData.DateTaken = subIfdDirectory.SafeGetExifDateTime(ExifDirectoryBase.TagDateTimeOriginal);

                        imgMetaData.Width = subIfdDirectory.SafeGetExifInt(ExifDirectoryBase.TagExifImageHeight);
                        imgMetaData.Height = subIfdDirectory.SafeGetExifInt(ExifDirectoryBase.TagExifImageWidth);

                        if (imgMetaData.Width == 0)
                            imgMetaData.Width = subIfdDirectory.SafeGetExifInt(ExifDirectoryBase.TagImageWidth);
                        if (imgMetaData.Height == 0)
                            imgMetaData.Height = subIfdDirectory.SafeGetExifInt(ExifDirectoryBase.TagImageHeight);

                        imgMetaData.ISO = subIfdDirectory.SafeExifGetString(ExifDirectoryBase.TagIsoEquivalent);
                        imgMetaData.FNum = subIfdDirectory.SafeExifGetString(ExifDirectoryBase.TagFNumber);
                        imgMetaData.Exposure = subIfdDirectory.SafeExifGetString(ExifDirectoryBase.TagExposureTime);

                        var lensMake = subIfdDirectory.SafeExifGetString(ExifDirectoryBase.TagLensMake);
                        var lensModel = subIfdDirectory.SafeExifGetString(ExifDirectoryBase.TagLensModel);
                        var lensSerial = subIfdDirectory.SafeExifGetString(ExifDirectoryBase.TagLensSerialNumber);

                        if (!string.IsNullOrEmpty(lensMake) || !string.IsNullOrEmpty(lensModel))
                            imgMetaData.LensId = GetLens(lensMake, lensModel, lensSerial).LensId;

                        var flash = subIfdDirectory.SafeGetExifInt(ExifDirectoryBase.TagFlash);

                        imgMetaData.FlashFired = ((flash & 0x1) != 0x0);
                    }

                    var IPTCdir = metadata.OfType<IptcDirectory>().FirstOrDefault();

                    if (IPTCdir != null)
                    {
                        var caption = IPTCdir.SafeExifGetString(IptcDirectory.TagCaption);

                        imgMetaData.Caption = FilteredDescription(caption);

                        // Stash the keywords in the dict, they'll be stored later.
                        var keywordList = IPTCdir?.GetStringArray(IptcDirectory.TagKeywords);
                        if (keywordList != null)
                            keywords = keywordList;
                    }

                    var IfdDirectory = metadata.OfType<ExifIfd0Directory>().FirstOrDefault();

                    if (IfdDirectory != null)
                    {
                        var camMake = IfdDirectory.SafeExifGetString(ExifDirectoryBase.TagMake);
                        var camModel = IfdDirectory.SafeExifGetString(ExifDirectoryBase.TagModel);
                        var camSerial = IfdDirectory.SafeExifGetString(ExifDirectoryBase.TagBodySerialNumber);

                        if (!string.IsNullOrEmpty(camMake) || !string.IsNullOrEmpty(camModel))
                        {
                            imgMetaData.CameraId = GetCamera(camMake, camModel, camSerial).CameraId;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Log("Error reading image metadata for {0}: {1}", image.FullPath, ex.Message);
            }
        }

        private string FilteredDescription(string desc)
        {
            if (! string.IsNullOrEmpty(desc))
            {
                // No point clogging up the DB with thousands
                // of identical default descriptions
                if (desc.Trim().Equals("OLYMPUS DIGITAL CAMERA"))
                    return string.Empty;
            }

            return desc;
        }

        #region Tag, Lens and Camera Caching
        /// <summary>
        /// Get a camera object, for each make/model. Uses an in-memory cache for speed.
        /// </summary>
        /// <param name="make"></param>
        /// <param name="model"></param>
        /// <returns></returns>
        private Camera GetCamera( string make, string model, string serial)
        {
            if (_cameraCache == null)
            {
                using var db = new ImageContext();
                _cameraCache = new ConcurrentDictionary<string, Camera>( db.Cameras
                                                                           .AsNoTracking() // We never update, so this is faster
                                                                           .ToDictionary(x => x.Make + x.Model, y => y) );
            }

            string cacheKey = make + model;

            if (string.IsNullOrEmpty(cacheKey))
                return null;

            if (!_cameraCache.TryGetValue(cacheKey, out Camera cam))
            {
                // It's a new one.
                cam = new Camera { Make = make, Model = model, Serial = serial };

                using var db = new ImageContext();
                db.Cameras.Add(cam);
                db.SaveChanges("SaveCamera");

                _cameraCache[cacheKey] = cam;
            }

            return cam;
        }

        /// <summary>
        /// Get a lens object, for each make/model. Uses an in-memory cache for speed.
        /// </summary>
        /// <param name="make"></param>
        /// <param name="model"></param>
        /// <returns></returns>
        private Lens GetLens(string make, string model, string serial)
        {
            if (_lensCache == null)
            {
                using ImageContext db = new ImageContext();
                _lensCache = new ConcurrentDictionary<string, Lens>(db.Lenses
                                                                      .AsNoTracking()
                                                                      .ToDictionary(x => x.Make + x.Model, y => y)) ;
            }

            string cacheKey = make + model;

            if (string.IsNullOrEmpty(cacheKey))
                return null;

            if (!_lensCache.TryGetValue(cacheKey, out Lens lens))
            {
                // It's a new one.
                lens = new Lens { Make = make, Model = model, Serial = serial };

                using var db = new ImageContext();
                db.Lenses.Add(lens);
                db.SaveChanges("SaveLens");

                _lensCache[cacheKey] = lens;
            }

            return lens;
        }

        /// <summary>
        /// Initialise the in-memory cache of tags.
        /// </summary>
        /// <param name="force"></param>
        private void LoadTagCache(bool force = false)
        {
            try
            {
                if (_tagCache == null || force)
                {
                    var watch = new Stopwatch("LoadTagCache");

                    using (var db = new ImageContext())
                    {
                        // Pre-cache tags from DB.
                        _tagCache = new ConcurrentDictionary<string, Models.Tag>(db.Tags
                                                                                    .AsNoTracking()
                                                                                    .ToDictionary(k => k.Keyword, v => v));
                        if (_tagCache.Any())
                            Logging.LogTrace("Pre-loaded cach with {0} tags.", _tagCache.Count());
                    }

                    watch.Stop();
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Unexpected exception loading tag cache: {ex.Message}");
            }
        }
        #endregion

        /// <summary>
        /// Given a collection of images and their keywords, performs a bulk insert
        /// of them all. This is way more performant than adding the keywords as
        /// each image is indexed, and allows us to bulk-update the freetext search
        /// too.
        /// </summary>
        /// <param name="imageKeywords"></param>
        private void AddTags( IDictionary<Image, string[]> imageKeywords )
        {
            // See if we have any images that were written to the DB and have IDs
            if ( ! imageKeywords.Where( x => x.Key.ImageId != 0 ).Any())
                return;

            var watch = new Stopwatch("AddTags");

            using ImageContext db = new ImageContext();

            try
            {
                var newTags = imageKeywords.Where( x => x.Value != null && x.Value.Any() )
                                        .SelectMany(x => x.Value)
                                        .Distinct()
                                        .Where( x => _tagCache != null && ! _tagCache.ContainsKey( x ))
                                        .Select( x => new Models.Tag { Keyword = x, Type = "IPTC" })
                                        .ToList();


                if (newTags.Any())
                {

                    Logging.LogTrace("Adding {0} tags", newTags.Count());

                    var config = new BulkConfig { SetOutputIdentity = true };
                    db.BulkInsert(newTags, config);

                    // Add the new items to the cache. 
                    foreach (var tag in newTags)
                        _tagCache[tag.Keyword] = tag;
                }
            }
            catch (Exception ex)
            {
                Logging.LogError("Exception adding Tags: {0}", ex);
            }

            using (var transaction = db.Database.BeginTransaction())
            {
                try
                {
                    var newImageTags = imageKeywords.SelectMany(i => i.Value.Select(
                                                                    v => new ImageTag
                                                                    {
                                                                        ImageId = i.Key.ImageId,
                                                                        TagId = _tagCache[v].TagId
                                                                    }))
                                                                .ToList();

                    // Note that we need to delete all of the existing tags for an image,
                    // and then insert all of the new tags. This is so that if somebody adds
                    // one tag, and removes another, we maintain the list correctly.
                    Logging.LogTrace($"Updating {newImageTags.Count()} ImageTags");

                    db.ImageTags.Where(y => newImageTags.Select(x => x.ImageId).Contains(y.ImageId)).BatchDelete();
                    db.BulkInsertOrUpdate(newImageTags);

                    transaction.Commit();

                    db.FullTextTags(false);
                }
                catch (Exception ex)
                {
                    Logging.LogError("Exception adding ImageTags: {0}", ex);
                }
            }
            watch.Stop();
        }

        /// <summary>
        /// Indexes all of the images in a folder, optionally filtering for a last-mod
        /// threshold and only indexing those images which have changed since that date.
        /// </summary>
        /// <param name="folder"></param>
        /// <param name="threshold"></param>
        /// <param name="parent"></param>
        public void IndexFolder(DirectoryInfo folder, Folder parent )
        {
            Folder folderToScan = null;

            // Get all the sub-folders on the disk, but filter out
            // ones we're not interested in.
            var subFolders = folder.SafeGetSubDirectories()
                                    .Where( x => x.IsMonitoredFolder() )
                                    .ToList();

            try
            {
                using (var db = new ImageContext())
                {
                    // Load the existing folder and its images from the DB
                    folderToScan = db.Folders
                                .Where(x => x.Path.Equals(folder.FullName))
                                .Include(x => x.Images)
                                .FirstOrDefault();

                    if (folderToScan == null)
                    {
                        Logging.LogVerbose("Scanning new folder: {0}\\{1}", folder.Parent.Name, folder.Name);
                        folderToScan = new Folder { Path = folder.FullName };
                    }
                    else
                        Logging.LogVerbose("Scanning existing folder: {0}\\{1} ({2} images in DB)", folder.Parent.Name, folder.Name, folderToScan.Images.Count());

                    if (parent != null)
                        folderToScan.ParentFolderId = parent.FolderId;

                    bool foldersChanged = false;

                    if (folderToScan.FolderId == 0)
                    {
                        Logging.Log($"Adding new folder: {folderToScan.Path}");
                        // New folder, add it. 
                        db.Folders.Add(folderToScan);
                        db.SaveChanges("AddFolders");
                        foldersChanged = true;
                    }

                    // Now query the DB for child folders of our current folder
                    var dbChildDirs = db.Folders.Where(x => x.ParentFolderId == folderToScan.FolderId).ToList();

                    // ...and then look for any DB folders that aren't included in the list of sub-folders.
                    // That means they've been removed from the disk, and should be removed from the DB.
                    var missingDirs = dbChildDirs.Where(f => !subFolders.Select(x => x.FullName)
                                                                        .Contains(f.Path, StringComparer.OrdinalIgnoreCase))
                                                                        .ToList();

                    if (missingDirs.Any())
                    {
                        missingDirs.ForEach(x => Logging.LogVerbose("Deleting folder {0}", x.Path));
                        missingDirs.ForEach(x => RemoveFileWatcher(x.Path));

                        db.RemoveRange(missingDirs);

                        Logging.Log("Removing {0} deleted folders...", missingDirs.Count());
                        // TODO: Use bulk delete?
                        db.SaveChanges("DeleteFolders");
                        foldersChanged = true;
                    }

                    if (foldersChanged)
                        NotifyFolderChanged();
                }

                // Now scan the images:
                ScanFolderImages( folderToScan );

                CreateFileWatcher(folder);
            }
            catch (Exception ex)
            {
                Logging.LogError($"Unexpected exception scanning folder {folderToScan.Name}: {ex.Message}");
            }

            // Scan subdirs recursively.
            foreach (var sub in subFolders)
            {
                IndexFolder(sub, folderToScan);
            }
        }

        /// <summary>
        /// For a given folder, scans the disk to find all the images in that folder,
        /// and then indexes all of those images for metadata etc. Optionally takes
        /// a last-mod threshold which, if set, will mean that only images changed
        /// since that date will be processed.
        /// </summary>
        /// <param name="folderToScan"></param>
        /// <param name="force">Force the folder to be scanned</param>
        /// <returns></returns>
        private bool ScanFolderImages(Folder folderToScan, bool force = false)
        {
            int folderImageCount = 0;

            if( folderToScan.FolderScanDate != null && ! force )
            {
                return true;
            }

            var folder = new DirectoryInfo(folderToScan.Path);
            var allImageFiles = folder.SafeGetImageFiles();

            if (allImageFiles == null)
            {
                // Null here means we weren't able to read the contents of the directory.
                // So bail, and give up on this folder altogether.
                return false;
            }

            using (var db = new ImageContext())
            {
                var watch = new Stopwatch("ScanFolderFiles");

                // Select just JPGs
                var imageFiles = allImageFiles.Where(x => x.IsImageFileType() ).ToList();
                folderImageCount = imageFiles.Count();

                int newImages = 0, updatedImages = 0;
                foreach (var file in imageFiles)
                {
                    try
                    {
                        var existingImage = folderToScan.Images.FirstOrDefault(x => x.FileName.Equals(file.Name));

                        if (existingImage != null && file.WriteTimesMatch(existingImage.FileLastModDate))
                        {
                            Logging.LogTrace("Indexed image {0} unchanged - skipping.", existingImage.FileName);
                            continue;
                        }

                        Image image = existingImage;

                        if (image == null)
                        {
                            image = new Image { FileName = file.Name };
                        }

                        // Store some info about the disk file
                        image.FileSizeBytes = (ulong)file.Length;
                        image.FileCreationDate = file.CreationTimeUtc;
                        image.FileLastModDate = file.LastWriteTimeUtc;
                        image.Folder = folderToScan;
                        image.LastUpdated = DateTime.UtcNow;

                        if (existingImage == null)
                        {
                            Logging.LogTrace("Adding new image {0}", image.FileName);
                            folderToScan.Images.Add(image);
                            newImages++;
                        }
                        else
                        {
                            db.Images.Update(image);
                            updatedImages++;
                        }
                    }
                    catch( Exception ex )
                    {
                        Logging.LogError($"Exception while scanning for new image {file}: {ex.Message}");
                    }
                }

                // Now look for files to remove.
                // TODO - Sanity check that these don't hit the DB
                var filesToRemove = folderToScan.Images.Select(x => x.FileName).Except(imageFiles.Select(x => x.Name));
                var dbImages = folderToScan.Images.Select(x => x.FileName);
                var imagesToDelete = folderToScan.Images
                                    .Where(x => filesToRemove.Contains(x.FileName))
                                    .ToList();

                if (imagesToDelete.Any())
                {
                    imagesToDelete.ForEach(x => Logging.LogVerbose("Deleting image {0} (ID: {1})", x.FileName, x.ImageId));

                    // Removing these will remove the associated ImageTag and selection references.
                    db.Images.RemoveRange(imagesToDelete);
                }

                // Now update the folder to say we've processed it
                folderToScan.FolderScanDate = DateTime.UtcNow;
                db.Folders.Update(folderToScan);

                db.SaveChanges("FolderImageScan");

                watch.Stop();

                StatusService.Instance.StatusText = string.Format("Indexed folder {0}: processed {1} images ({2} new, {3} updated, {4} removed) in {5}.",
                        folderToScan.Name,folderToScan.Images.Count(), newImages, updatedImages, imagesToDelete.Count(), watch.HumanElapsedTime);
            }

            return true;
        }

        public bool IndexFolder(Folder folder)
        {
            return ScanFolderImages(folder);
        }

        public void PerformMetaDataScan()
        {
            Logging.LogVerbose("Full Metadata scan starting...");

            try
            {
                var watch = new Stopwatch("FullMetaDataScan", -1);

                using var db = new ImageContext();

                const int batchSize = 100;
                bool complete = false;

                while (!complete)
                {
                    var queueQueryWatch = new Stopwatch("MetaDataQueueQuery", -1);

                    // Find all images where there's either no metadata, or where the image
                    // was updated more recently than the image metadata
                    var imagesToScan = db.Images.Where( x => x.MetaData == null || x.MetaData.LastUpdated < x.LastUpdated )
                                            .OrderByDescending( x => x.LastUpdated )
                                            .Take(batchSize)
                                            .Include(x => x.Folder)
                                            .Include(x => x.MetaData)
                                            .ToArray();

                    queueQueryWatch.Stop();

                    complete = !imagesToScan.Any();

                    if (!complete)
                    {
                        var batchWatch = new Stopwatch("MetaDataBatch", 100000);

                        // Aggregate stuff that we'll collect up as we scan
                        var imageKeywords = new ConcurrentDictionary<Image, string[]>();

                        var newMetadataEntries = new List<ImageMetaData>();
                        var updatedEntries = new List<ImageMetaData>();

                        foreach (var img in imagesToScan)
                        {
                            ImageMetaData imgMetaData = img.MetaData;

                            if (imgMetaData == null)
                            {
                                // New metadata
                                imgMetaData = new ImageMetaData { ImageId = img.ImageId, Image = img };
                                newMetadataEntries.Add(imgMetaData);
                            }
                            else
                                updatedEntries.Add(imgMetaData);

                            GetImageMetaData(ref imgMetaData, out var keywords);

                            if (keywords.Any())
                                imageKeywords[img] = keywords;

                            // Yield a bit. TODO: must be a better way of doing this
                            Thread.Sleep(50);
                        }

                        var saveWatch = new Stopwatch("MetaDataSave");
                        Logging.LogTrace($"Adding {newMetadataEntries.Count()} and updating {updatedEntries.Count()} metadata entries.");

                        // TODO: For some reason BulkInsertOrUpdate re-inserts the
                        // same record every time with a key of zero. 
                        db.BulkInsert( newMetadataEntries );
                        db.BulkUpdate( updatedEntries );

                        saveWatch.Stop();

                        var tagWatch = new Stopwatch("AddTagsSave");

                        // Now save the tags
                        AddTags( imageKeywords );

                        tagWatch.Stop();

                        batchWatch.Stop();

                        Logging.Log($"Completed metadata scan batch ({imagesToScan.Length} images in {batchWatch.HumanElapsedTime}, save: {saveWatch.HumanElapsedTime}, tags: {tagWatch.HumanElapsedTime}).");
                    }
                }

                watch.Stop();
            }
            catch( Exception ex )
            {
                Logging.LogError($"Exception caught during metadata scan: {ex}");
            }

            Logging.LogVerbose("Metadata Scan Complete.");
        }

        public void StartService()
        {
            Logging.Log("Started indexing service.");
            StartIndexingThread();
        }

        private void StartIndexingThread()
        {
            var indexthread = new Thread( new ThreadStart(() => { RunIndexing(); } ));
            indexthread.Name = "IndexingThread";
            indexthread.IsBackground = true;
            indexthread.Priority = ThreadPriority.Lowest;
            indexthread.Start();

            var metathread = new Thread(new ThreadStart(() => { RunMetaDataScans(); }));
            metathread.Name = "MetaDataThread";
            metathread.IsBackground = true;
            metathread.Priority = ThreadPriority.Lowest;
            metathread.Start();
        }

        public void PerformFullIndex()
        {
            // Perform a full index at startup
            StatusService.Instance.StatusText = "Full Indexing starting...";
            var root = new DirectoryInfo(RootFolder);

            var watch = new Stopwatch("CompleteIndex", -1);

            IndexFolder(root, null);

            watch.Stop();

            StatusService.Instance.StatusText = "Full Indexing Complete.";
        }

        private void RunIndexing()
        {
            LoadTagCache();

            PerformFullIndex();

            while ( true )
            {
                Logging.LogVerbose("Polling for pending folder index changes.");

                using var db = new ImageContext();

                const int batchSize = 50;

                // First, take all the queued folder changes and persist them to the DB
                // by setting the FolderScanDate to null.
                var folders = new List<string>();

                while (folderQueue.TryDequeue(out var folder))
                {
                    Logging.Log($"Flagging change for folder: {folder}");
                    folders.Add(folder);
                }

                if( folders.Any() )
                {
                    // Now, update any folders to set their scan date to null
                    var pendingFolders = db.Folders
                                           .Where(f => folders.Contains(f.Path))
                                           .BatchUpdate( f => new Folder { FolderScanDate = null } );
                }

                // Now, see if there's any folders that have a null scan date.
                var foldersToIndex = db.Folders.Where(x => x.FolderScanDate == null)
                                               .Take( batchSize )
                                               .ToList();

                if( foldersToIndex.Any() )
                {
                    StatusService.Instance.StatusText = $"Detected {foldersToIndex.Count()} folders with new/changed images.";

                    foreach ( var folder in foldersToIndex )
                    {
                        var dir = new DirectoryInfo(folder.Path);
                        // Scan the folder for subdirs
                        IndexFolder(dir, null);
                    }
                }

                Thread.Sleep(1000 * 30);
            }
        }

        private void RunMetaDataScans()
        {
            while (true)
            {
                // This should have its own thread. 
                PerformMetaDataScan();
                // TODO: Shouldn't need this
                Thread.Sleep(1000 * 60);
            }
        }

        private void RemoveFileWatcher( string path )
        {
            if( _watchers.TryGetValue( path, out var fsw ) )
            {
                Logging.Log($"Removing FileWatcher for {path}");

                _watchers.Remove(path);

                fsw.EnableRaisingEvents = false;
                fsw.Changed -= OnChanged;
                fsw.Created -= OnChanged;
                fsw.Deleted -= OnChanged;
                fsw.Renamed -= OnRenamed;
                fsw.Error -= WatcherError;
                fsw = null;
            }
        }

        private static ConcurrentQueue<string> folderQueue = new ConcurrentQueue<string>();

        private void CreateFileWatcher(DirectoryInfo path)
        {
            if (!_watchers.ContainsKey(path.FullName) )
            {
                try
                {
                    var watcher = new FileSystemWatcher();

                    Logging.LogVerbose($"Creating FileWatcher for {path}");

                    watcher.Path = path.FullName;

                    // Watch for changes in LastAccess and LastWrite
                    // times, and the renaming of files.
                    watcher.NotifyFilter = NotifyFilters.LastWrite
                                          | NotifyFilters.FileName
                                          | NotifyFilters.Size
                                          | NotifyFilters.DirectoryName;

                    // Add event handlers.
                    watcher.Changed += OnChanged;
                    watcher.Created += OnChanged;
                    watcher.Deleted += OnChanged;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += WatcherError;

                    // Store it in the map
                    _watchers[path.FullName] = watcher;

                    // Begin watching.
                    watcher.EnableRaisingEvents = true;
                }
                catch( Exception ex )
                {
                    Logging.LogError($"Exception creating filewatcher for {path}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Process disk-level inotify changes. Note that this should be *very*
        /// fast to keep up with updates as they come in. So we put all distinct
        /// changes into a queue and then return, and the queue contents will be
        /// processed in batch later. This has the effect of us being able to
        /// collect up a conflated list of actual changes with minimal blocking.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="changeType"></param>
        private static void FlagFolderForRescan( FileInfo file, WatcherChangeTypes changeType )
        {
            using var db = new ImageContext();

            var folder = file.Directory.FullName;

            // If it's hidden, or already in the queue, ignore it.
            if (file.IsHidden() || folderQueue.Contains(folder))
                return;

            // Ignore non images, and hidden files/folders.
            if (file.IsDirectory() || file.IsImageFileType())
            {
                Logging.Log($"FileWacher: adding to queue: {folder} {changeType}");
                folderQueue.Enqueue(folder);
            }
        }

        private static void WatcherError(object sender, ErrorEventArgs e)
        {
            // TODO - need to catch many of these and abort - if the inotify count is too large
            Logging.LogError($"Flagging Error for folder: {e.GetException().Message}");
        }

        private static void OnChanged(object source, FileSystemEventArgs e)
        {
            Logging.LogVerbose($"FileWacher: {e.FullPath} {e.ChangeType}");

            var file = new FileInfo(e.FullPath);

            FlagFolderForRescan(file, e.ChangeType);
        }

        private static void OnRenamed(object source, RenamedEventArgs e)
        {
            Logging.LogVerbose($"FileWatcher: {e.OldFullPath} => {e.FullPath} {e.ChangeType}");

            var oldfile = new FileInfo(e.OldFullPath);
            var newfile = new FileInfo(e.FullPath);

            FlagFolderForRescan(oldfile, e.ChangeType);
            FlagFolderForRescan(newfile, e.ChangeType);
        }
    }
}