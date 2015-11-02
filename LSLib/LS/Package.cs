﻿using zlib;
using LZ4;
using LSLib.Granny;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LSLib.LS
{
    internal struct LSPKHeader13
    {
        public UInt32 Version;
        public UInt32 FileListOffset;
        public UInt32 FileListSize;
        public UInt16 NumParts;
        public UInt16 SomePartVar;
        public Guid ArchiveGuid;
    }

    internal struct FileEntry13
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] Name;
        public UInt32 OffsetInFile;
        public UInt32 SizeOnDisk;
        public UInt32 UncompressedSize;
        public UInt32 ArchivePart;
        public UInt32 Flags;
        public UInt32 Crc;
    }

    abstract public class FileInfo
    {
        public String Name;

        abstract public UInt32 Size();
        abstract public UInt32 CRC();
        abstract public BinaryReader MakeReader();
    }

    public class PackagedFileInfo : FileInfo
    {
        public Stream PackageStream;
        public UInt32 OffsetInFile;
        public UInt32 SizeOnDisk;
        public UInt32 UncompressedSize;
        public UInt32 ArchivePart;
        public UInt32 Flags;
        public UInt32 Crc;

        public override UInt32 Size()
        {
            if ((Flags & 0x0F) == 0)
                return SizeOnDisk;
            else
                return UncompressedSize;
        }

        public override UInt32 CRC()
        {
            return Crc;
        }

        public override BinaryReader MakeReader()
        {
            var compressed = new byte[SizeOnDisk];

            this.PackageStream.Seek(OffsetInFile, SeekOrigin.Begin);
            int readSize = this.PackageStream.Read(compressed, 0, (int)SizeOnDisk);
            if (readSize != SizeOnDisk)
            {
                var msg = String.Format("Failed to read {0} bytes from archive (only got {1})", SizeOnDisk, readSize);
                throw new InvalidDataException(msg);
            }

            var computedCrc = Crc32.Compute(compressed);
            if (computedCrc != Crc)
            {
                var msg = String.Format(
                    "CRC check failed on file '{0}', archive is possibly corrupted. Expected {1,8:X}, got {2,8:X}",
                    Name, Crc, computedCrc
                );
                throw new InvalidDataException(msg);
            }

            var uncompressed = BinUtils.Decompress(compressed, (int)Size(), (byte)Flags);
            var memStream = new MemoryStream(uncompressed);
            var reader = new BinaryReader(memStream);
            return reader;
        }

        internal static PackagedFileInfo CreateFromEntry(FileEntry13 entry, Stream dataStream)
        {
            var info = new PackagedFileInfo();
            info.PackageStream = dataStream;

            var nameLen = 0;
            for (nameLen = 0; nameLen < entry.Name.Length && entry.Name[nameLen] != 0; nameLen++) { }
            info.Name = Encoding.UTF8.GetString(entry.Name, 0, nameLen);

            var compressionMethod = entry.Flags & 0x0F;
            if (compressionMethod > 2 || (entry.Flags & ~0x7F) != 0)
            {
                var msg = String.Format("File '{0}' has unsupported flags: {1}", info.Name, entry.Flags);
                throw new InvalidDataException(msg);
            }

            info.OffsetInFile = entry.OffsetInFile;
            info.SizeOnDisk = entry.SizeOnDisk;
            info.UncompressedSize = entry.UncompressedSize;
            info.ArchivePart = entry.ArchivePart;
            info.Flags = entry.Flags;
            info.Crc = entry.Crc;
            return info;
        }

        internal FileEntry13 MakeEntry()
        {
            var entry = new FileEntry13();
            entry.Name = new byte[256];
            var encodedName = Encoding.UTF8.GetBytes(Name.Replace('\\', '/'));
            Array.Copy(encodedName, entry.Name, encodedName.Length);

            entry.OffsetInFile = OffsetInFile;
            entry.SizeOnDisk = SizeOnDisk;
            entry.UncompressedSize = ((Flags & 0x0F) == 0) ? 0 : UncompressedSize;
            entry.ArchivePart = ArchivePart;
            entry.Flags = Flags;
            entry.Crc = Crc;
            return entry;
        }
    }

    public class FilesystemFileInfo : FileInfo
    {
        public string FilesystemPath;
        public long CachedSize;

        public override UInt32 Size()
        {
            return (UInt32)CachedSize;
        }

        public override UInt32 CRC()
        {
            throw new NotImplementedException("!");
        }

        public override BinaryReader MakeReader()
        {
            var fs = new FileStream(FilesystemPath, FileMode.Open, FileAccess.Read);
            return new BinaryReader(fs);
        }

        public static FilesystemFileInfo CreateFromEntry(string filesystemPath, string name)
        {
            var info = new FilesystemFileInfo();
            info.Name = name;
            info.FilesystemPath = filesystemPath;

            var fsInfo = new System.IO.FileInfo(filesystemPath);
            info.CachedSize = fsInfo.Length;
            return info;
        }
    }

    public class Package
    {
        public static byte[] Signature = new byte[] { 0x4C, 0x53, 0x50, 0x4B };
        public static UInt32 CurrentVersion = 0x0D;

        internal List<FileInfo> Files = new List<FileInfo>();

        public static string MakePartFilename(string path, int part)
        {
            var dirName = Path.GetDirectoryName(path);
            var baseName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            return String.Format("{0}/{1}_{2}{3}", dirName, baseName, part, extension);
        }
    }

    public class Packager
    {
        public delegate void ProgressUpdateDelegate(string status, long numerator, long denominator);
        public ProgressUpdateDelegate progressUpdate = delegate { };

        private void WriteProgressUpdate(FileInfo file, long numerator, long denominator)
        {
            this.progressUpdate(file.Name, numerator, denominator);
        }

        public void UncompressPackage(string packagePath, string outputPath)
        {
            if (outputPath.Length > 0 && outputPath.Last() != '/' && outputPath.Last() != '\\')
                outputPath += "/";

            this.progressUpdate("Reading package headers ...", 0, 1);
            var reader = new PackageReader(packagePath);
            var package = reader.Read();

            long totalSize = package.Files.Sum(p => (long)p.Size());
            long currentSize = 0;

            foreach (var file in package.Files)
            {
                this.progressUpdate(file.Name, currentSize, totalSize);
                currentSize += file.Size();

                var outPath = outputPath + file.Name;
                var dirName = Path.GetDirectoryName(outPath);
                if (!Directory.Exists(dirName))
                {
                    Directory.CreateDirectory(dirName);
                }

                var inReader = file.MakeReader();
                var outFile = new FileStream(outPath, FileMode.Create, FileAccess.Write);

                if (inReader != null)
                {
                    byte[] buffer = new byte[32768];
                    int read;
                    while ((read = inReader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        outFile.Write(buffer, 0, read);
                    }

                    inReader.Dispose();
                }

                outFile.Dispose();
            }

            reader.Dispose();
        }

        public void EnumerateFiles(Package package, string rootPath, string currentPath)
        {
            foreach (string filePath in Directory.GetFiles(currentPath))
            {
                var relativePath = filePath.Substring(rootPath.Length);
                if (relativePath[0] == '/' || relativePath[0] == '\\')
                {
                    relativePath = relativePath.Substring(1);
                }

                var fileInfo = FilesystemFileInfo.CreateFromEntry(filePath, relativePath);
                package.Files.Add(fileInfo);
            }

            foreach (string directoryPath in Directory.GetDirectories(currentPath))
            {
                EnumerateFiles(package, rootPath, directoryPath);
            }
        }

        public void CreatePackage(string packagePath, string inputPath, CompressionMethod compression = CompressionMethod.None, bool fastCompression = true)
        {
            this.progressUpdate("Enumerating files ...", 0, 1);
            var package = new Package();
            EnumerateFiles(package, inputPath, inputPath);

            this.progressUpdate("Creating archive ...", 0, 1);
            var writer = new PackageWriter(package, packagePath);
            writer.writeProgress += WriteProgressUpdate;
            writer.Compression = compression;
            writer.CompressionLevel = fastCompression ? LS.CompressionLevel.FastCompression : LS.CompressionLevel.DefaultCompression;
            writer.Write();
            writer.Dispose();
        }
    }

    public class PackageWriter
    {
        public delegate void WriteProgressDelegate(FileInfo file, long numerator, long denominator);
        public WriteProgressDelegate writeProgress = delegate { };
        public CompressionMethod Compression = CompressionMethod.None;
        public CompressionLevel CompressionLevel = CompressionLevel.DefaultCompression;

        private static long MaxPackageSize = 0x40000000;

        private Package package;
        private String path;
        private List<Stream> streams = new List<Stream>();

        public PackageWriter(Package package, string path)
        {
            this.package = package;
            this.path = path;
        }

        public void Dispose()
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }

        public PackagedFileInfo WriteFile(FileInfo info)
        {
            // Assume that all files are written uncompressed (worst-case) when calculating package sizes
            var size = info.Size();
            if (streams.Last().Position + size > MaxPackageSize)
            {
                // Start a new package file if the current one is full.
                var partPath = Package.MakePartFilename(path, streams.Count);
                var nextPart = new FileStream(partPath, FileMode.Create, FileAccess.Write);
                streams.Add(nextPart);
            }

            var stream = streams.Last();
            var packaged = new PackagedFileInfo();
            packaged.PackageStream = stream;
            packaged.Name = info.Name;
            packaged.UncompressedSize = size;
            packaged.ArchivePart = (UInt32)(streams.Count - 1);
            packaged.OffsetInFile = (UInt32)stream.Position;
            packaged.Flags = BinUtils.MakeCompressionFlags(Compression, CompressionLevel);

            var reader = info.MakeReader();
            var uncompressed = reader.ReadBytes((int)reader.BaseStream.Length);
            var compressed = BinUtils.Compress(uncompressed, Compression, CompressionLevel);
            stream.Write(compressed, 0, compressed.Length);
            reader.Dispose();

            packaged.SizeOnDisk = (UInt32)(stream.Position - packaged.OffsetInFile);
            packaged.Crc = Crc32.Compute(compressed);

            if (stream.Position % 0x40 > 0)
            {
                // Pad the file to a multiple of 64 bytes
                byte[] pad = new byte[0x40 - (stream.Position % 0x40)];
                for (int i = 0; i < pad.Length; i++)
                {
                    pad[i] = 0xAD;
                }

                stream.Write(pad, 0, pad.Length);
            }

            return packaged;
        }

        public void Write()
        {
            var mainStream = new FileStream(path, FileMode.Create, FileAccess.Write);
            streams.Add(mainStream);

            long totalSize = package.Files.Sum(p => (long)p.Size());
            long currentSize = 0;
            List<PackagedFileInfo> writtenFiles = new List<PackagedFileInfo>();
            foreach (var file in this.package.Files)
            {
                writeProgress(file, currentSize, totalSize);
                writtenFiles.Add(WriteFile(file));
                currentSize += file.Size();
            }

            using (var writer = new BinaryWriter(mainStream, new UTF8Encoding(), true))
            {
                var header = new LSPKHeader13();
                header.Version = Package.CurrentVersion;
                header.FileListOffset = (UInt32)mainStream.Position;

                writer.Write((UInt32)writtenFiles.Count);

                var fileList = new MemoryStream();
                var fileListWriter = new BinaryWriter(fileList);
                foreach (var file in writtenFiles)
                {
                    var entry = file.MakeEntry();
                    BinUtils.WriteStruct<FileEntry13>(fileListWriter, ref entry);
                }

                var fileListBuf = fileList.ToArray();
                fileListWriter.Dispose();
                var compressedFileList = LZ4Codec.EncodeHC(fileListBuf, 0, fileListBuf.Length);

                writer.Write(compressedFileList);

                header.FileListSize = (UInt32)mainStream.Position - header.FileListOffset;
                header.NumParts = (UInt16)streams.Count;
                header.SomePartVar = 0; // ???
                header.ArchiveGuid = Guid.NewGuid();
                BinUtils.WriteStruct<LSPKHeader13>(writer, ref header);

                writer.Write((UInt32)(8 + Marshal.SizeOf(typeof(LSPKHeader13))));
                writer.Write(Package.Signature);
            }
        }
    }

    public class PackageReader
    {
        private String path;
        private Stream[] streams;

        public PackageReader(string path)
        {
            this.path = path;
        }

        public void Dispose()
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }

        public Package Read()
        {
            var package = new Package();
            var mainStream = new FileStream(path, FileMode.Open, FileAccess.Read);

            using (var reader = new BinaryReader(mainStream, new UTF8Encoding(), true))
            {
                mainStream.Seek(-8, SeekOrigin.End);
                Int32 headerSize = reader.ReadInt32();
                byte[] signature = reader.ReadBytes(4);
                if (!Package.Signature.SequenceEqual(signature))
                {
                    throw new InvalidDataException("Package file has an invalid signature");
                }

                mainStream.Seek(-headerSize, SeekOrigin.End);

                mainStream.Seek(-headerSize, SeekOrigin.End);
                var header = BinUtils.ReadStruct<LSPKHeader13>(reader);

                if (header.Version != Package.CurrentVersion)
                {
                    var msg = String.Format("Unsupported package version {0}; this extractor only supports {1}", header.Version, Package.CurrentVersion);
                    throw new InvalidDataException(msg);
                }

                // Open a stream for each file chunk
                streams = new Stream[header.NumParts];
                streams[0] = mainStream;

                for (int part = 1; part < header.NumParts; part++ )
                {
                    var partPath = Package.MakePartFilename(path, part);
                    streams[part] = new FileStream(partPath, FileMode.Open, FileAccess.Read);
                }

                mainStream.Seek(header.FileListOffset, SeekOrigin.Begin);
                int numFiles = reader.ReadInt32();
                int fileBufferSize = Marshal.SizeOf(typeof(FileEntry13)) * numFiles;
                byte[] compressedFileList = reader.ReadBytes((int)header.FileListSize - 4);

                var uncompressedList = new byte[fileBufferSize];
                var uncompressedSize = LZ4Codec.Decode(compressedFileList, 0, compressedFileList.Length, uncompressedList, 0, fileBufferSize, true);
                if (uncompressedSize != fileBufferSize)
                {
                    var msg = String.Format("LZ4 compressor disagrees about the size of file headers; expected {0}, got {1}", fileBufferSize, uncompressedSize);
                    throw new InvalidDataException(msg);
                }

                var ms = new MemoryStream(uncompressedList);
                var msr = new BinaryReader(ms);

                for (int i = 0; i < numFiles; i++)
                {
                    var entry = BinUtils.ReadStruct<FileEntry13>(msr);
                    package.Files.Add(PackagedFileInfo.CreateFromEntry(entry, streams[entry.ArchivePart]));
                }
            }

            return package;
        }
    }
}