﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DokanNet;
using SevenZip;
using System.Threading;
using SharpCompress.Archives.Rar;

namespace Shaman.Dokan
{
    public class MyMirror : ReadOnlyFs
    {
        private string path = "";

        FsNode<FileInfo> root;

        private string[] excludes = new string[] {"$RECYCLE.BIN", "System Volume Information"};

        public MyMirror(string path)
        {
            this.path = path.TrimEnd('\\');

            root = GetFileInfo(this.path);

            FileSystemWatcher fsw = new FileSystemWatcher(this.path, "*");
            fsw.IncludeSubdirectories = true;
            //fsw.Changed += Fsw_Changed;
            fsw.Created += Fsw_Changed;
            fsw.Renamed += Fsw_Changed;
            fsw.Deleted += Fsw_Changed;
            fsw.EnableRaisingEvents = true;

            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(5000);
                    //Console.WriteLine("*******");
                    //Status();
                    foreach (var item in MemoryStreamInternal.instances.ToArray())
                    {
                        Console.WriteLine("MemoryStreamInternal: {0} len: {1} datalen: {2}", item.Filename, item.Length, item.data?.Length);
                    }
                }
            }) { IsBackground = true }.Start();
        }

        private void Fsw_Changed(object sender, FileSystemEventArgs e)
        {
            foreach (var exclude in excludes)
            {
                if (e.FullPath.Contains(exclude))
                    return;
            }

            Console.WriteLine("File Changed {0} {1}", e.FullPath, e.ChangeType.ToString());

            var path = Path.GetDirectoryName(e.FullPath).ToLower().Replace(root.FullName.ToLower(), "");

            if (path == "")
                return;

            // invalidate cache
            var file = GetNode(root, path);
            if (file != null)
            {
                file.Children = null;
            }
            var dir = GetFileInfo(Path.GetDirectoryName(e.FullPath));
            if (dir != null)
            {
                dir.Children = null;
                Console.WriteLine("Children set to null for " + dir.FullName);
            }
            else
            {
                Console.WriteLine("Failed to invalidate children " + dir);
            }
        }

        public override string SimpleMountName => "MyMirror-" + path;

        DokanNet.Logging.ILogger logger = new DokanNet.Logging.ConsoleLogger("[MyMirror]");

        public override NtStatus CreateFile(string fileName, DokanNet.FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, DokanFileInfo info)
        {
            var tid = Thread.CurrentThread.ManagedThreadId;
           
            logger.Debug(tid+" CreateFileProxy : {0}", fileName);
         /*   logger.Debug(tid + " \tCreationDisposition\t{0}", (FileMode)mode);
            logger.Debug(tid + " \tFileAccess\t{0}", (DokanNet.FileAccess)access);
            logger.Debug(tid + " \tFileShare\t{0}", (FileShare)share);
            logger.Debug(tid + " \tFileOptions\t{0}", options);
            logger.Debug(tid + " \tFileAttributes\t{0}", attributes);
            logger.Debug(tid + " \tContext\t{0}", info);
            */
            if (IsBadName(fileName)) return NtStatus.ObjectNameInvalid;
            if ((access & ModificationAttributes) != 0) return NtStatus.DiskFull;

            var item = GetFile(fileName);
            if (item == null) return DokanResult.FileNotFound;
            if (item.Info.FullName != null && !isDirectory(item))
            {
                if ((access & (DokanNet.FileAccess.ReadData | DokanNet.FileAccess.GenericRead) ) != 0)
                {
                    Console.WriteLine("MyMirror ReadData: " + fileName);

                    var archive = item.Tag as FsNode<RarArchiveEntry>;

                    if (archive != null)
                    {
                        var idx = fileName.ToLower().IndexOf(archive.FullName.ToLower());
                        var file = fileName.Substring(0, idx-1);

                        try
                        {
                            var af = cache[path.ToLower() + file.ToLower()].CreateFile(archive.FullName, access, share, mode, options,
                                attributes,
                                info);

                            return af;
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine(ex);
                            Console.ForegroundColor = ConsoleColor.White;
                            return NtStatus.AccessDenied;
                        }
                    }
                    else
                    {
                        info.Context =
                            File.Open(path +
                                          fileName, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
                    }

                }
                return NtStatus.Success;
            }
            else
            {
                info.IsDirectory = true;
                return NtStatus.Success;
            }
        }

        ConcurrentDictionary<string, SharpCompressFs> cache = new ConcurrentDictionary<string, SharpCompressFs>();


        public FsNode<FileInfo> GetFile(string fileName)
        {
            return GetNode(root, fileName);
        }

        void Status()
        {
            var memory = GC.GetTotalMemory(false);

            foreach (var item in cache.ToArray())
            {
                /*foreach (var item2 in item.Value.cache.streams.ToArray())
                {
                    logger.Debug("Status: {0} {1} {2} {3} {4} {5} {6}", memory / 1024 / 1024, item2.Value.Filename,
                        item.Key, "", item2.Key.FullName, item2.Value.ms.data?.Length, item2.Value.Lastread);
                }*/
            }
        }
        
        private FsNode<FileInfo> GetFileInfo(string fileName, FileInfo prefileinfo = null)
        {
            logger.Debug(Thread.CurrentThread.ManagedThreadId +" GetFileInfo: " + fileName);

            if (!File.Exists(fileName) && !Directory.Exists(fileName) ||
                fileName.ToLower().EndsWith(".rar") || fileName.ToLower().EndsWith(".zip"))
            {
                if (fileName.ToLower().Contains(".rar") || fileName.ToLower().Contains(".zip"))
                {
                    try
                    {
                        var info2 = new FileInfo(fileName);

                        var index = fileName.ToLower().IndexOf(".rar");
                        if (index == -1)
                            index = fileName.ToLower().IndexOf(".zip");

                        var file = fileName.Substring(0, index + 4);
                        var subpath = fileName.Substring(index + 4);

                        SharpCompressFs fs;
                        cache.TryGetValue(file.ToLower(), out fs);
                        if (fs == null)
                        {
                            logger.Debug("SevenZipFs: get list " + file);
                            try
                            {
                                fs = new SharpCompressFs(file);
                            }
                            catch
                            {
                                return null;
                            }
                        }

                        cache[file.ToLower()] = fs;
                        var fsnodeinfo = fs.GetFile(subpath);

                        if (fsnodeinfo == null)
                            return null;

                        var answerarchive = new FsNode<FileInfo>()
                        {
                            Tag = fsnodeinfo,
                            Info = info2,
                            Name = info2.Name,
                            FullName = info2.FullName,
                        };

                        answerarchive.GetChildrenDelegate = () =>
                        {
                            answerarchive.Tag = fsnodeinfo;

                            return fsnodeinfo?.Children?
                                .Select(x => GetFileInfo((
                                    file + Path.DirectorySeparatorChar + subpath +
                                    Path.DirectorySeparatorChar +
                                    x).Replace(""+Path.DirectorySeparatorChar + Path.DirectorySeparatorChar,
                                    ""+Path.DirectorySeparatorChar)))?.Where(a => a != null).ToList();
                        };

                        return answerarchive;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(ex);
                        Console.ForegroundColor = ConsoleColor.White;
                        return null;
                    }
                }

                return null;
            }

            FileInfo info;
            if (prefileinfo != null)
                info = prefileinfo;
            else
                info = new FileInfo(fileName);

            var answer = new FsNode<FileInfo>()
            {
                Info = info,
                Name = info.Name,
                FullName = info.FullName,
                GetChildrenDelegate = () =>
                {
                    DirectoryInfo dirinfo = new DirectoryInfo(info.FullName);

                    if (dirinfo.Exists)
                    {
                        // get dirs
                        var dirs = new ConcurrentBag<FsNode<FileInfo>>();
                        Parallel.ForEach(dirinfo.GetDirectories("*", SearchOption.TopDirectoryOnly), x =>
                        {
                            if (CheckHasValidChildren(x, "*.rar"))
                                dirs.Add(GetFileInfo(x.FullName));
                        });

                        // get files
                        var files = new ConcurrentBag<FsNode<FileInfo>>();
                        var fileInfos = dirinfo.GetFiles("*.rar", SearchOption.TopDirectoryOnly);
                        Parallel.ForEach(fileInfos, x =>
                        {
                            files.Add(GetFileInfo(x.FullName, x));
                        });
                        // filter files
                        var filefilter = files.Where(a =>
                            a != null &&
                            (a.Name.ToLower().Contains("part01.rar") || !a.Name.ToLower().Contains(".part")));

                        // combine to one list
                        var combined = filefilter.Concat(dirs).Where(a => a != null);
                        // return result
                        return combined.ToList();
                    }
                    return null;
                }
            };

            return answer;
        }

        private bool CheckHasValidChildren(DirectoryInfo x, string v, int level = 0)
        {
            try
            {
                var dirs = x.GetDirectories("*", SearchOption.TopDirectoryOnly);

                var files = x.GetFiles(v, SearchOption.TopDirectoryOnly);

                // we have files in this dir
                if (files.Length > 0)
                    return true;

                if (dirs.Length == 0)
                    return false;

                // to save time show the dir if its deep
                if (level >= 1) // base/1/2/*
                    return true;

                foreach (var dir in dirs)
                {
                    var local = CheckHasValidChildren(dir, v, level + 1);
                    if (local)
                        return true;
                }
            }
            catch { }

            return false;
        }

        public override NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, DokanFileInfo info)
        {
            logger.Debug(Thread.CurrentThread.ManagedThreadId+" GetFileInformation<NtStatus>: {0} ", fileName);

            var item = GetFile(fileName);
            if (item == null)
            {
                fileInfo = default(FileInformation);
                logger.Debug("GetFileInformation<NtStatus>: {0} FileNotFound", fileName);
                return DokanResult.FileNotFound;
            }
            fileInfo = GetFileInformation(item);

            logger.Debug("GetFileInformation<NtStatus>: {0} Success", fileName);
            return NtStatus.Success;
        }

        bool isDirectory(FsNode<FileInfo> info)
        {
            if (info.Info.Exists || File.Exists(info.FullName))
            {
                if (info.Name.ToLower().EndsWith(".rar"))
                    return true;
                if (info.Name.ToLower().EndsWith(".zip"))
                    return true;
                if (info.Name.ToLower().EndsWith(".7z"))
                    return true;
                if (info.Name.ToLower().EndsWith(".iso"))
                    return true;
            }
            if (info.Tag is FsNode<RarArchiveEntry>)
            {
                //var children = info.GetChildrenDelegate();

                if (((FsNode<RarArchiveEntry>) info.Tag).Info.IsDirectory)
                    return true;
                return false;
            }
            return (info.Info.Attributes & FileAttributes.Directory) > 0;
        }

        FileInformation GetFileInformation(FsNode<FileInfo> item)
        {
            logger.Debug(Thread.CurrentThread.ManagedThreadId+" GetFileInformation<FsNode>: {0} {1}" ,item.FullName, item.Info.Attributes);
            if (item == null)
                return new FileInformation();

            var isdir = isDirectory(item);
            long length = isdir ? 0 : item.Info.Exists ? item.Info.Length : 0;
            FileAttributes attrib = item.Info.Attributes;

            var archive = item.Tag as FsNode<RarArchiveEntry>;

            if (archive != null && archive.Info != null)
            {
                length = (long) archive.Info.Size;
                attrib = FileAttributes.Normal;
                return new FileInformation()
                {
                    FileName = item.Info.Name,
                    Length = length,
                    Attributes = isdir ? (FileAttributes.Directory) : attrib,
                    CreationTime = archive.Info.CreatedTime,
                    LastAccessTime = archive.Info.LastAccessedTime,
                    LastWriteTime = archive.Info.LastModifiedTime,
                };
            }

            return new FileInformation()
            {
                FileName = item.Info.Name,
                Length = length,
                Attributes = isdir ? (FileAttributes.Directory) : attrib,
                CreationTime = item.Info.CreationTime,
                LastAccessTime = item.Info.LastAccessTime,
                LastWriteTime = item.Info.LastWriteTime,
            };
        }

        public override NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, DokanFileInfo info)
        {
            fileSystemName = volumeLabel = "MyMirror";
            features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.ReadOnlyVolume | FileSystemFeatures.UnicodeOnDisk | FileSystemFeatures.VolumeIsCompressed;
            return NtStatus.Success;
        }

        public override NtStatus GetDiskFreeSpace(out long free, out long total, out long used, DokanFileInfo info)
        {
            free = 0;
            total = 1024L*1024*1024*1024;
            used = total;

            return NtStatus.Success;

            //return base.GetDiskFreeSpace(out free, out total, out used, info);
        }

        protected override IList<FileInformation> FindFilesHelper(string fileName, string searchPattern)
        {
            try
            {
                logger.Debug("FindFilesHelper: '{0}' '{1}'", fileName, searchPattern);
                
                var item = GetFile(fileName);
                if (item == null) return null;

                if (isDirectory(item))
                {
                    if (item.Children == null) return new FileInformation[] { };
                    var matcher = GetMatcher(searchPattern);
                    var list = new ConcurrentBag<FileInformation>();
                    var where = item.Children.Where(x => x != null && matcher(x.Name));
                    Parallel.ForEach(where, x => { list.Add(GetFileInformation(x)); });
                    return list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(ex);
                Console.ForegroundColor = ConsoleColor.White;
            }

            return new List<FileInformation>();
        }

        public override void Cleanup(string fileName, DokanFileInfo info)
        {
        }
    }
}
