using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Contracts;

namespace DoNetUp.IO.Compression
{
    public static class ZipFileEx
    {
        private const char PathSeparator = '/';

        /// <summary>
        /// <p>Creates a Zip archive stream that contains the files and directories from
        /// the directory specified by <code>sourceDirectoryName</code>. The directory structure is preserved in the archive, and a
        /// recursive search is done for files to be archived. If the directory is empty, an empty
        /// archive will be created. If a file in the directory cannot be added to the archive, the archive will be left incomplete
        /// and invalid and the method will throw an exception. This method does not include the base directory into the archive.
        /// If an error is encountered while adding files to the archive, this method will stop adding files and leave the archive
        /// in an invalid state. The paths are permitted to specify relative or absolute path information. Relative path information
        /// is interpreted as relative to the current working directory. If a file in the archive has data in the last write time
        /// field that is not a valid Zip timestamp, an indicator value of 1980 January 1 at midnight will be used for the file's
        /// last modified time.</p>
        /// 
        /// <p>If an entry with the specified name already exists in the archive, a second entry will be created that has an identical name.</p>
        /// 
        /// <p>Since no <code>CompressionLevel</code> is specified, the default provided by the implementation of the underlying compression
        /// algorithm will be used; the <code>ZipArchive</code> will not impose its own default.
        /// (Currently, the underlying compression algorithm is provided by the <code>System.IO.Compression.DeflateStream</code> class.)</p>
        /// </summary>
        /// 
        /// <exception cref="ArgumentException"><code>sourceDirectoryName</code> or <code>destinationArchiveFileName</code> is a zero-length
        ///                                     string, contains only white space, or contains one or more invalid characters as defined by
        ///                                     <code>InvalidPathChars</code>.</exception>
        /// <exception cref="ArgumentNullException"><code>sourceDirectoryName</code> or <code>destinationArchiveFileName</code> is null.</exception>
        /// <exception cref="PathTooLongException">In <code>sourceDirectoryName</code> or <code>destinationArchiveFileName</code>, the specified
        ///                                        path, file name, or both exceed the system-defined maximum length.
        ///                                        For example, on Windows-based platforms, paths must be less than 248 characters, and file
        ///                                        names must be less than 260 characters.</exception>
        /// <exception cref="DirectoryNotFoundException">The path specified in <code>sourceDirectoryName</code>
        ///                                              is invalid, (for example, it is on an unmapped drive).
        ///                                              -OR- The directory specified by <code>sourceDirectoryName</code> does not exist.</exception>
        /// <exception cref="NotSupportedException"><code>sourceDirectoryName</code> is in an invalid format.</exception>
        ///                                         
        /// <param name="sourceDirectoryName">The path to the directory on the file system to be archived. </param>
        /// <returns>The stream of archive.</returns>
        public static Stream CreateStreamFromDirectory(string sourceDirectoryName)
        {
            return DoCreateStreamFromDirectory(sourceDirectoryName,
                      compressionLevel: null, includeBaseDirectory: false, entryNameEncoding: null);
        }

        private static void EnsureCapacity(ref char[] buffer, int min)
        {
            Debug.Assert(buffer != null);
            Debug.Assert(min > 0);

            if (buffer.Length < min)
            {
                int newCapacity = buffer.Length * 2;
                if (newCapacity < min)
                    newCapacity = min;
                ArrayPool<char>.Shared.Return(buffer);
                buffer = ArrayPool<char>.Shared.Rent(newCapacity);
            }
        }

        private static string EntryFromPath(string entry, int offset, int length, ref char[] buffer, bool appendPathSeparator = false)
        {
            Debug.Assert(length <= entry.Length - offset);
            Debug.Assert(buffer != null);

            // Remove any leading slashes from the entry name:
            while (length > 0)
            {
                if (entry[offset] != Path.DirectorySeparatorChar &&
                    entry[offset] != Path.AltDirectorySeparatorChar)
                    break;

                offset++;
                length--;
            }

            if (length == 0)
                return appendPathSeparator ? PathSeparator.ToString() : string.Empty;

            int resultLength = appendPathSeparator ? length + 1 : length;
            EnsureCapacity(ref buffer, resultLength);
            entry.CopyTo(offset, buffer, 0, length);

            // '/' is a more broadly recognized directory separator on all platforms (eg: mac, linux)
            // We don't use Path.DirectorySeparatorChar or AltDirectorySeparatorChar because this is
            // explicitly trying to standardize to '/'
            for (int i = 0; i < length; i++)
            {
                char ch = buffer[i];
                if (ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar)
                    buffer[i] = PathSeparator;
            }

            if (appendPathSeparator)
                buffer[length] = PathSeparator;

            return new string(buffer, 0, resultLength);
        }

        private static Stream DoCreateStreamFromDirectory(string sourceDirectoryName,
                                          CompressionLevel? compressionLevel, bool includeBaseDirectory,
                                          Encoding entryNameEncoding)
        {
            // Rely on Path.GetFullPath for validation of sourceDirectoryName and destinationArchive

            // Checking of compressionLevel is passed down to DeflateStream and the IDeflater implementation
            // as it is a pluggable component that completely encapsulates the meaning of compressionLevel.

            sourceDirectoryName = Path.GetFullPath(sourceDirectoryName);

            var memoryStream = new MemoryStream();

            using (ZipArchive archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
            {
                bool directoryIsEmpty = true;

                // Add files and directories
                DirectoryInfo di = new DirectoryInfo(sourceDirectoryName);

                string basePath = di.FullName;

                if (includeBaseDirectory && di.Parent != null)
                {
                    basePath = di.Parent.FullName;
                }

                // Windows' MaxPath (260) is used as an arbitrary default capacity, as it is likely
                // to be greater than the length of typical entry names from the file system, even
                // on non-Windows platforms. The capacity will be increased, if needed.
                const int DefaultCapacity = 260;
                char[] entryNameBuffer = ArrayPool<char>.Shared.Rent(DefaultCapacity);

                try
                {
                    foreach (FileSystemInfo file in di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                    {
                        directoryIsEmpty = false;

                        int entryNameLength = file.FullName.Length - basePath.Length;
                        Debug.Assert(entryNameLength > 0);

                        if (file is FileInfo)
                        {
                            // Create entry for file:
                            string entryName = EntryFromPath(file.FullName, basePath.Length, entryNameLength, ref entryNameBuffer);
                            DoCreateEntryFromFile(archive, file.FullName, entryName, compressionLevel);
                        }
                        else
                        {
                            // Entry marking an empty dir:
                            DirectoryInfo possiblyEmpty = file as DirectoryInfo;
                            if (possiblyEmpty != null && IsDirEmpty(possiblyEmpty))
                            {
                                // FullName never returns a directory separator character on the end,
                                // but Zip archives require it to specify an explicit directory:
                                string entryName = EntryFromPath(file.FullName, basePath.Length, entryNameLength, ref entryNameBuffer, appendPathSeparator: true);
                                archive.CreateEntry(entryName);
                            }
                        }
                    }  // foreach

                    // If no entries create an empty root directory entry:
                    if (includeBaseDirectory && directoryIsEmpty)
                        archive.CreateEntry(EntryFromPath(di.Name, 0, di.Name.Length, ref entryNameBuffer, appendPathSeparator: true));
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(entryNameBuffer);
                }
            } // using archive

            memoryStream.Seek(0, SeekOrigin.Begin);

            return memoryStream;
        }  // DoCreateStreamFromDirectory

        private static bool IsDirEmpty(DirectoryInfo possiblyEmptyDir)
        {
            using (IEnumerator<string> enumerator = Directory.EnumerateFileSystemEntries(possiblyEmptyDir.FullName).GetEnumerator())
            {
                return !enumerator.MoveNext();
            }
        }

        private static ZipArchiveEntry DoCreateEntryFromFile(ZipArchive destination,
                                                              string sourceFileName, string entryName, CompressionLevel? compressionLevel)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            if (sourceFileName == null)
                throw new ArgumentNullException(nameof(sourceFileName));

            if (entryName == null)
                throw new ArgumentNullException(nameof(entryName));

            // Checking of compressionLevel is passed down to DeflateStream and the IDeflater implementation
            // as it is a pluggable component that completely encapsulates the meaning of compressionLevel.

            // Argument checking gets passed down to FileStream's ctor and CreateEntry
            Contract.Ensures(Contract.Result<ZipArchiveEntry>() != null);
            Contract.EndContractBlock();

            using (Stream fs = new FileStream(sourceFileName, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0x1000, useAsync: false))
            {
                ZipArchiveEntry entry = compressionLevel.HasValue
                                                ? destination.CreateEntry(entryName, compressionLevel.Value)
                                                : destination.CreateEntry(entryName);

                DateTime lastWrite = File.GetLastWriteTime(sourceFileName);

                // If file to be archived has an invalid last modified time, use the first datetime representable in the Zip timestamp format
                // (midnight on January 1, 1980):
                if (lastWrite.Year < 1980 || lastWrite.Year > 2107)
                {
                    lastWrite = new DateTime(1980, 1, 1, 0, 0, 0);
                }

                entry.LastWriteTime = lastWrite;

                using (Stream es = entry.Open())
                {
                    fs.CopyTo(es);
                }

                return entry;
            }
        }
    }
}
