using System.Text;

namespace DirForge.Services;

/// <summary>
/// Detects file types by reading magic bytes from the file header.
/// Returns a human-readable description or null if the format is unrecognized.
/// </summary>
public static class FileSignatureDetector
{
    private const int HeaderSize = 384; // covers TAR ustar at 257, MPEG-TS sync at 376, NIfTI at 344

    public static string? Detect(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[HeaderSize];
            var bytesRead = fs.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0) return null;

            return Match(buffer, bytesRead);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Match(byte[] buf, int len)
    {
        // ── Images ──────────────────────────────────────────────────────

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (len >= 8 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47
            && buf[4] == 0x0D && buf[5] == 0x0A && buf[6] == 0x1A && buf[7] == 0x0A)
            return "PNG Image";

        // MNG: 8A 4D 4E 47 0D 0A 1A 0A
        if (len >= 8 && buf[0] == 0x8A && buf[1] == 0x4D && buf[2] == 0x4E && buf[3] == 0x47
            && buf[4] == 0x0D && buf[5] == 0x0A && buf[6] == 0x1A && buf[7] == 0x0A)
            return "MNG Image";

        // JPEG: FF D8 FF
        if (len >= 3 && buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF)
            return "JPEG Image";

        // GIF87a / GIF89a
        if (len >= 6 && buf[0] == 0x47 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x38
            && (buf[4] == 0x37 || buf[4] == 0x39) && buf[5] == 0x61)
            return "GIF Image";

        // RIFF-based: WebP, WAV, AVI, ANI
        if (len >= 12 && buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46)
        {
            if (buf[8] == 0x57 && buf[9] == 0x45 && buf[10] == 0x42 && buf[11] == 0x50)
                return "WebP Image";
            if (buf[8] == 0x57 && buf[9] == 0x41 && buf[10] == 0x56 && buf[11] == 0x45)
                return "WAV Audio";
            if (buf[8] == 0x41 && buf[9] == 0x56 && buf[10] == 0x49 && buf[11] == 0x20)
                return "AVI Video";
            if (buf[8] == 0x41 && buf[9] == 0x43 && buf[10] == 0x4F && buf[11] == 0x4E)
                return "ANI Animated Cursor";
        }

        // TIFF LE: 49 49 2A 00
        if (len >= 4 && buf[0] == 0x49 && buf[1] == 0x49 && buf[2] == 0x2A && buf[3] == 0x00)
            return "TIFF Image";

        // TIFF BE: 4D 4D 00 2A
        if (len >= 4 && buf[0] == 0x4D && buf[1] == 0x4D && buf[2] == 0x00 && buf[3] == 0x2A)
            return "TIFF Image";

        // ftyp-based: AVIF, HEIF/HEIC first (more specific), then generic MP4/MOV
        if (len >= 12 && buf[4] == 0x66 && buf[5] == 0x74 && buf[6] == 0x79 && buf[7] == 0x70)
        {
            var brand = Encoding.ASCII.GetString(buf, 8, 4);
            if (brand is "avif" or "avis")
                return "AVIF Image";
            if (brand is "heic" or "heix" or "mif1" or "hevc")
                return "HEIF Image";
            if (brand is "crx ")
                return "Canon RAW Image";
            return "MP4/MOV Video";
        }

        // JPEG XL container: 00 00 00 0C 4A 58 4C 20 0D 0A 87 0A
        if (len >= 12 && buf[0] == 0x00 && buf[1] == 0x00 && buf[2] == 0x00 && buf[3] == 0x0C
            && buf[4] == 0x4A && buf[5] == 0x58 && buf[6] == 0x4C && buf[7] == 0x20
            && buf[8] == 0x0D && buf[9] == 0x0A && buf[10] == 0x87 && buf[11] == 0x0A)
            return "JPEG XL Image";

        // JPEG 2000 container: 00 00 00 0C 6A 50 20 20 0D 0A 87 0A
        if (len >= 12 && buf[0] == 0x00 && buf[1] == 0x00 && buf[2] == 0x00 && buf[3] == 0x0C
            && buf[4] == 0x6A && buf[5] == 0x50 && buf[6] == 0x20 && buf[7] == 0x20
            && buf[8] == 0x0D && buf[9] == 0x0A && buf[10] == 0x87 && buf[11] == 0x0A)
            return "JPEG 2000 Image";

        // JPEG 2000 codestream: FF 4F FF 51
        if (len >= 4 && buf[0] == 0xFF && buf[1] == 0x4F && buf[2] == 0xFF && buf[3] == 0x51)
            return "JPEG 2000 Image";

        // ICO: 00 00 01 00
        if (len >= 4 && buf[0] == 0x00 && buf[1] == 0x00 && buf[2] == 0x01 && buf[3] == 0x00)
            return "ICO Icon";

        // CUR: 00 00 02 00
        if (len >= 4 && buf[0] == 0x00 && buf[1] == 0x00 && buf[2] == 0x02 && buf[3] == 0x00)
            return "CUR Cursor";

        // PSD: 8BPS
        if (len >= 4 && buf[0] == 0x38 && buf[1] == 0x42 && buf[2] == 0x50 && buf[3] == 0x53)
            return "Photoshop Document";

        // OpenEXR: 76 2F 31 01
        if (len >= 4 && buf[0] == 0x76 && buf[1] == 0x2F && buf[2] == 0x31 && buf[3] == 0x01)
            return "OpenEXR Image";

        // GIMP XCF: "gimp xcf"
        if (len >= 8 && buf[0] == 0x67 && buf[1] == 0x69 && buf[2] == 0x6D && buf[3] == 0x70
            && buf[4] == 0x20 && buf[5] == 0x78 && buf[6] == 0x63 && buf[7] == 0x66)
            return "GIMP XCF Image";

        // QOI: "qoif"
        if (len >= 4 && buf[0] == 0x71 && buf[1] == 0x6F && buf[2] == 0x69 && buf[3] == 0x66)
            return "QOI Image";

        // FLIF: "FLIF"
        if (len >= 4 && buf[0] == 0x46 && buf[1] == 0x4C && buf[2] == 0x49 && buf[3] == 0x46)
            return "FLIF Image";

        // JPEG XL codestream: FF 0A (must be after JPEG check to avoid false matches)
        if (len >= 2 && buf[0] == 0xFF && buf[1] == 0x0A)
            return "JPEG XL Image";

        // ── Documents ───────────────────────────────────────────────────

        // PDF: %PDF-
        if (len >= 5 && buf[0] == 0x25 && buf[1] == 0x50 && buf[2] == 0x44 && buf[3] == 0x46 && buf[4] == 0x2D)
            return "PDF Document";

        // RTF: {\rtf
        if (len >= 5 && buf[0] == 0x7B && buf[1] == 0x5C && buf[2] == 0x72 && buf[3] == 0x74 && buf[4] == 0x66)
            return "RTF Document";

        // PostScript: %!PS
        if (len >= 4 && buf[0] == 0x25 && buf[1] == 0x21 && buf[2] == 0x50 && buf[3] == 0x53)
            return "PostScript Document";

        // Microsoft OLE2 Compound File (DOC/XLS/PPT/MSI): D0 CF 11 E0 A1 B1 1A E1
        if (len >= 8 && buf[0] == 0xD0 && buf[1] == 0xCF && buf[2] == 0x11 && buf[3] == 0xE0
            && buf[4] == 0xA1 && buf[5] == 0xB1 && buf[6] == 0x1A && buf[7] == 0xE1)
            return "Microsoft Office Document";

        // MOBI eBook: "BOOKMOBI"
        if (len >= 8 && buf[0] == 0x42 && buf[1] == 0x4F && buf[2] == 0x4F && buf[3] == 0x4B
            && buf[4] == 0x4D && buf[5] == 0x4F && buf[6] == 0x42 && buf[7] == 0x49)
            return "MOBI eBook";

        // DjVu: "AT&TFORM"
        if (len >= 8 && buf[0] == 0x41 && buf[1] == 0x54 && buf[2] == 0x26 && buf[3] == 0x54
            && buf[4] == 0x46 && buf[5] == 0x4F && buf[6] == 0x52 && buf[7] == 0x4D)
            return "DjVu Document";

        // CHM: "ITSF"
        if (len >= 4 && buf[0] == 0x49 && buf[1] == 0x54 && buf[2] == 0x53 && buf[3] == 0x46)
            return "CHM Help File";

        // ── Databases / Data ────────────────────────────────────────────

        // SQLite: "SQLite format 3\0"
        if (len >= 16)
        {
            var sqliteMagic = "SQLite format 3\0"u8;
            if (buf.AsSpan(0, 16).SequenceEqual(sqliteMagic))
                return "SQLite Database";
        }

        // Apache Parquet: "PAR1"
        if (len >= 4 && buf[0] == 0x50 && buf[1] == 0x41 && buf[2] == 0x52 && buf[3] == 0x31)
            return "Apache Parquet Data";

        // HDF5: 89 48 44 46 0D 0A 1A 0A
        if (len >= 8 && buf[0] == 0x89 && buf[1] == 0x48 && buf[2] == 0x44 && buf[3] == 0x46
            && buf[4] == 0x0D && buf[5] == 0x0A && buf[6] == 0x1A && buf[7] == 0x0A)
            return "HDF5 Data";

        // Apache Avro: "Obj\x01"
        if (len >= 4 && buf[0] == 0x4F && buf[1] == 0x62 && buf[2] == 0x6A && buf[3] == 0x01)
            return "Apache Avro Data";

        // Apache Arrow IPC: "ARROW1"
        if (len >= 6 && buf[0] == 0x41 && buf[1] == 0x52 && buf[2] == 0x52 && buf[3] == 0x4F
            && buf[4] == 0x57 && buf[5] == 0x31)
            return "Apache Arrow Data";

        // Apache ORC: "ORC"
        if (len >= 3 && buf[0] == 0x4F && buf[1] == 0x52 && buf[2] == 0x43)
            return "Apache ORC Data";

        // Hadoop SequenceFile: "SEQ" + version 4/5/6
        if (len >= 4 && buf[0] == 0x53 && buf[1] == 0x45 && buf[2] == 0x51
            && buf[3] is 0x04 or 0x05 or 0x06)
            return "Hadoop SequenceFile";

        // ── Archives / Compression ──────────────────────────────────────

        // RAR5: Rar!\x1A\x07\x01\x00
        if (len >= 8 && buf[0] == 0x52 && buf[1] == 0x61 && buf[2] == 0x72 && buf[3] == 0x21
            && buf[4] == 0x1A && buf[5] == 0x07 && buf[6] == 0x01 && buf[7] == 0x00)
            return "RAR Archive";

        // RAR4: Rar!\x1A\x07\x00
        if (len >= 7 && buf[0] == 0x52 && buf[1] == 0x61 && buf[2] == 0x72 && buf[3] == 0x21
            && buf[4] == 0x1A && buf[5] == 0x07 && buf[6] == 0x00)
            return "RAR Archive";

        // 7z: 37 7A BC AF 27 1C
        if (len >= 6 && buf[0] == 0x37 && buf[1] == 0x7A && buf[2] == 0xBC && buf[3] == 0xAF
            && buf[4] == 0x27 && buf[5] == 0x1C)
            return "7-Zip Archive";

        // XZ: FD 37 7A 58 5A 00
        if (len >= 6 && buf[0] == 0xFD && buf[1] == 0x37 && buf[2] == 0x7A && buf[3] == 0x58
            && buf[4] == 0x5A && buf[5] == 0x00)
            return "XZ Archive";

        // Zstandard: 28 B5 2F FD
        if (len >= 4 && buf[0] == 0x28 && buf[1] == 0xB5 && buf[2] == 0x2F && buf[3] == 0xFD)
            return "Zstandard Archive";

        // Bzip2: 42 5A 68
        if (len >= 3 && buf[0] == 0x42 && buf[1] == 0x5A && buf[2] == 0x68)
            return "Bzip2 Archive";

        // LZ4 frame: 04 22 4D 18
        if (len >= 4 && buf[0] == 0x04 && buf[1] == 0x22 && buf[2] == 0x4D && buf[3] == 0x18)
            return "LZ4 Archive";

        // LZMA: 5D 00 00
        if (len >= 3 && buf[0] == 0x5D && buf[1] == 0x00 && buf[2] == 0x00)
            return "LZMA Archive";

        // Lzip: "LZIP"
        if (len >= 4 && buf[0] == 0x4C && buf[1] == 0x5A && buf[2] == 0x49 && buf[3] == 0x50)
            return "Lzip Archive";

        // LZOP: 89 4C 5A 4F 00 0D 0A 1A 0A
        if (len >= 9 && buf[0] == 0x89 && buf[1] == 0x4C && buf[2] == 0x5A && buf[3] == 0x4F
            && buf[4] == 0x00 && buf[5] == 0x0D && buf[6] == 0x0A && buf[7] == 0x1A && buf[8] == 0x0A)
            return "LZOP Archive";

        // Snappy framed: FF 06 00 00 73 4E 61 50 70 59
        if (len >= 10 && buf[0] == 0xFF && buf[1] == 0x06 && buf[2] == 0x00 && buf[3] == 0x00
            && buf[4] == 0x73 && buf[5] == 0x4E && buf[6] == 0x61 && buf[7] == 0x50
            && buf[8] == 0x70 && buf[9] == 0x59)
            return "Snappy Archive";

        // Microsoft Cabinet: "MSCF"
        if (len >= 4 && buf[0] == 0x4D && buf[1] == 0x53 && buf[2] == 0x43 && buf[3] == 0x46)
            return "Microsoft Cabinet Archive";

        // XAR archive: "xar!"
        if (len >= 4 && buf[0] == 0x78 && buf[1] == 0x61 && buf[2] == 0x72 && buf[3] == 0x21)
            return "XAR Archive";

        // Unix AR / Debian Package: "!<arch>\n"
        if (len >= 8 && buf[0] == 0x21 && buf[1] == 0x3C && buf[2] == 0x61 && buf[3] == 0x72
            && buf[4] == 0x63 && buf[5] == 0x68 && buf[6] == 0x3E && buf[7] == 0x0A)
        {
            // Check for "debian-binary" at offset 8 (first archive member filename)
            if (len >= 21 && buf.AsSpan(8, 13).SequenceEqual("debian-binary"u8))
                return "Debian Package";
            return "Unix AR Archive";
        }

        // RPM: ED AB EE DB
        if (len >= 4 && buf[0] == 0xED && buf[1] == 0xAB && buf[2] == 0xEE && buf[3] == 0xDB)
            return "RPM Package";

        // cpio newc: "070701" or CRC "070702"
        if (len >= 6 && buf[0] == 0x30 && buf[1] == 0x37 && buf[2] == 0x30 && buf[3] == 0x37
            && buf[4] == 0x30 && (buf[5] == 0x31 || buf[5] == 0x32))
            return "CPIO Archive";

        // cpio old ASCII: "070707"
        if (len >= 6 && buf.AsSpan(0, 6).SequenceEqual("070707"u8))
            return "CPIO Archive";

        // ZIP-based: EPUB/OpenDocument before generic ZIP (PK\x03\x04)
        if (len >= 4 && buf[0] == 0x50 && buf[1] == 0x4B && buf[2] == 0x03 && buf[3] == 0x04)
        {
            if (len >= 58 && buf.AsSpan(30, 8).SequenceEqual("mimetype"u8))
            {
                if (buf.AsSpan(38, 20).SequenceEqual("application/epub+zip"u8))
                    return "EPUB eBook";
                if (len >= 72 && buf.AsSpan(38, 34).StartsWith("application/vnd.oasis.opendocument"u8))
                    return "OpenDocument";
            }
            return "ZIP Archive";
        }

        // ZIP empty archive: PK\x05\x06
        if (len >= 4 && buf[0] == 0x50 && buf[1] == 0x4B && buf[2] == 0x05 && buf[3] == 0x06)
            return "ZIP Archive";

        // GZIP: 1F 8B
        if (len >= 2 && buf[0] == 0x1F && buf[1] == 0x8B)
            return "GZIP Archive";

        // Unix Compress: 1F 9D
        if (len >= 2 && buf[0] == 0x1F && buf[1] == 0x9D)
            return "Unix Compress Archive";

        // TAR: "ustar" at offset 257
        if (len >= 262 && buf[257] == 0x75 && buf[258] == 0x73 && buf[259] == 0x74
            && buf[260] == 0x61 && buf[261] == 0x72)
            return "TAR Archive";

        // ── Audio ───────────────────────────────────────────────────────

        // FLAC: fLaC
        if (len >= 4 && buf[0] == 0x66 && buf[1] == 0x4C && buf[2] == 0x61 && buf[3] == 0x43)
            return "FLAC Audio";

        // OGG: OggS
        if (len >= 4 && buf[0] == 0x4F && buf[1] == 0x67 && buf[2] == 0x67 && buf[3] == 0x53)
            return "OGG Audio";

        // AIFF/AIFF-C: FORM + AIFF/AIFC at offset 8
        if (len >= 12 && buf[0] == 0x46 && buf[1] == 0x4F && buf[2] == 0x52 && buf[3] == 0x4D)
        {
            if (buf[8] == 0x41 && buf[9] == 0x49 && buf[10] == 0x46 && buf[11] == 0x46)
                return "AIFF Audio";
            if (buf[8] == 0x41 && buf[9] == 0x49 && buf[10] == 0x46 && buf[11] == 0x43)
                return "AIFF Audio";
        }

        // DSDIFF: FRM8 + DSD at offset 12
        if (len >= 16 && buf[0] == 0x46 && buf[1] == 0x52 && buf[2] == 0x4D && buf[3] == 0x38)
        {
            if (buf[12] == 0x44 && buf[13] == 0x53 && buf[14] == 0x44 && buf[15] == 0x20)
                return "DSDIFF Audio";
        }

        // MIDI: "MThd"
        if (len >= 4 && buf[0] == 0x4D && buf[1] == 0x54 && buf[2] == 0x68 && buf[3] == 0x64)
            return "MIDI Audio";

        // WavPack: "wvpk"
        if (len >= 4 && buf[0] == 0x77 && buf[1] == 0x76 && buf[2] == 0x70 && buf[3] == 0x6B)
            return "WavPack Audio";

        // Musepack SV8: "MPCK"
        if (len >= 4 && buf[0] == 0x4D && buf[1] == 0x50 && buf[2] == 0x43 && buf[3] == 0x4B)
            return "Musepack Audio";

        // Musepack SV7: "MP+"
        if (len >= 3 && buf[0] == 0x4D && buf[1] == 0x50 && buf[2] == 0x2B)
            return "Musepack Audio";

        // Monkey's Audio: "MAC "
        if (len >= 4 && buf[0] == 0x4D && buf[1] == 0x41 && buf[2] == 0x43 && buf[3] == 0x20)
            return "Monkey's Audio";

        // DSF: "DSD "
        if (len >= 4 && buf[0] == 0x44 && buf[1] == 0x53 && buf[2] == 0x44 && buf[3] == 0x20)
            return "DSF Audio";

        // MP3: ID3 tag (49 44 33)
        if (len >= 3 && buf[0] == 0x49 && buf[1] == 0x44 && buf[2] == 0x33)
            return "MP3 Audio";

        // MPEG audio sync: differentiate MP3 (layer 2/3) from AAC ADTS (layer 0)
        if (len >= 2 && buf[0] == 0xFF && (buf[1] & 0xE0) == 0xE0)
        {
            var layerBits = (buf[1] >> 1) & 0x03;
            if (layerBits == 0x00)
                return "AAC Audio";
            if (layerBits is 0x01 or 0x02 or 0x03)
                return "MP3 Audio";
        }

        // AC-3 / Dolby Digital: 0B 77
        if (len >= 2 && buf[0] == 0x0B && buf[1] == 0x77)
            return "AC-3 Audio";

        // ── Video ───────────────────────────────────────────────────────

        // MKV/WebM: EBML 1A 45 DF A3
        if (len >= 4 && buf[0] == 0x1A && buf[1] == 0x45 && buf[2] == 0xDF && buf[3] == 0xA3)
            return "Matroska Video";

        // FLV: "FLV"
        if (len >= 3 && buf[0] == 0x46 && buf[1] == 0x4C && buf[2] == 0x56)
            return "FLV Video";

        // MPEG-TS: 0x47 sync byte at 188-byte intervals (0, 188, 376)
        if (len >= 377 && buf[0] == 0x47 && buf[188] == 0x47 && buf[376] == 0x47)
            return "MPEG Transport Stream";

        // MPEG Program Stream: 00 00 01 BA
        if (len >= 4 && buf[0] == 0x00 && buf[1] == 0x00 && buf[2] == 0x01 && buf[3] == 0xBA)
            return "MPEG Program Stream";

        // ASF/WMA/WMV: 30 26 B2 75 8E 66 CF 11 A6 D9 00 AA 00 62 CE 6C
        if (len >= 16 && buf[0] == 0x30 && buf[1] == 0x26 && buf[2] == 0xB2 && buf[3] == 0x75
            && buf[4] == 0x8E && buf[5] == 0x66 && buf[6] == 0xCF && buf[7] == 0x11
            && buf[8] == 0xA6 && buf[9] == 0xD9 && buf[10] == 0x00 && buf[11] == 0xAA
            && buf[12] == 0x00 && buf[13] == 0x62 && buf[14] == 0xCE && buf[15] == 0x6C)
            return "ASF Media";

        // RealMedia: ".RMF"
        if (len >= 4 && buf[0] == 0x2E && buf[1] == 0x52 && buf[2] == 0x4D && buf[3] == 0x46)
            return "RealMedia";

        // ── Disk Images / VM ────────────────────────────────────────────

        // QCOW2: QFI\xFB
        if (len >= 4 && buf[0] == 0x51 && buf[1] == 0x46 && buf[2] == 0x49 && buf[3] == 0xFB)
            return "QCOW2 Disk Image";

        // VMDK sparse: KDMV
        if (len >= 4 && buf[0] == 0x4B && buf[1] == 0x44 && buf[2] == 0x4D && buf[3] == 0x56)
            return "VMDK Disk Image";

        // VHD: "conectix"
        if (len >= 8 && buf[0] == 0x63 && buf[1] == 0x6F && buf[2] == 0x6E && buf[3] == 0x65
            && buf[4] == 0x63 && buf[5] == 0x74 && buf[6] == 0x69 && buf[7] == 0x78)
            return "VHD Disk Image";

        // VHDX: "vhdxfile"
        if (len >= 8 && buf[0] == 0x76 && buf[1] == 0x68 && buf[2] == 0x64 && buf[3] == 0x78
            && buf[4] == 0x66 && buf[5] == 0x69 && buf[6] == 0x6C && buf[7] == 0x65)
            return "VHDX Disk Image";

        // WIM: "MSWIM\0\0\0"
        if (len >= 8 && buf[0] == 0x4D && buf[1] == 0x53 && buf[2] == 0x57 && buf[3] == 0x49
            && buf[4] == 0x4D && buf[5] == 0x00 && buf[6] == 0x00 && buf[7] == 0x00)
            return "WIM Disk Image";

        // VDI: "<<< Oracle VM"
        if (len >= 13 && buf.AsSpan(0, 13).SequenceEqual("<<< Oracle VM"u8))
            return "VDI Disk Image";

        // SquashFS LE: "hsqs"
        if (len >= 4 && buf[0] == 0x68 && buf[1] == 0x73 && buf[2] == 0x71 && buf[3] == 0x73)
            return "SquashFS Image";

        // SquashFS BE: "sqsh"
        if (len >= 4 && buf[0] == 0x73 && buf[1] == 0x71 && buf[2] == 0x73 && buf[3] == 0x68)
            return "SquashFS Image";

        // CramFS BE: 45 3D CD 28
        if (len >= 4 && buf[0] == 0x45 && buf[1] == 0x3D && buf[2] == 0xCD && buf[3] == 0x28)
            return "CramFS Image";

        // CramFS LE: 28 CD 3D 45
        if (len >= 4 && buf[0] == 0x28 && buf[1] == 0xCD && buf[2] == 0x3D && buf[3] == 0x45)
            return "CramFS Image";

        // ── Fonts ───────────────────────────────────────────────────────

        // WOFF: "wOFF"
        if (len >= 4 && buf[0] == 0x77 && buf[1] == 0x4F && buf[2] == 0x46 && buf[3] == 0x46)
            return "WOFF Font";

        // WOFF2: "wOF2"
        if (len >= 4 && buf[0] == 0x77 && buf[1] == 0x4F && buf[2] == 0x46 && buf[3] == 0x32)
            return "WOFF2 Font";

        // OpenType/CFF: "OTTO"
        if (len >= 4 && buf[0] == 0x4F && buf[1] == 0x54 && buf[2] == 0x54 && buf[3] == 0x4F)
            return "OpenType Font";

        // TrueType Collection: "ttcf"
        if (len >= 4 && buf[0] == 0x74 && buf[1] == 0x74 && buf[2] == 0x63 && buf[3] == 0x66)
            return "TrueType Collection";

        // TrueType: 00 01 00 00 00
        if (len >= 5 && buf[0] == 0x00 && buf[1] == 0x01 && buf[2] == 0x00 && buf[3] == 0x00
            && buf[4] == 0x00)
            return "TrueType Font";

        // ── 3D Models ───────────────────────────────────────────────────

        // glTF Binary: "glTF"
        if (len >= 4 && buf[0] == 0x67 && buf[1] == 0x6C && buf[2] == 0x54 && buf[3] == 0x46)
            return "glTF 3D Model";

        // Blender: "BLENDER"
        if (len >= 7 && buf[0] == 0x42 && buf[1] == 0x4C && buf[2] == 0x45 && buf[3] == 0x4E
            && buf[4] == 0x44 && buf[5] == 0x45 && buf[6] == 0x52)
            return "Blender File";

        // FBX Binary: "Kaydara FBX Binary  "
        if (len >= 20 && buf.AsSpan(0, 20).SequenceEqual("Kaydara FBX Binary  "u8))
            return "FBX 3D Model";

        // PLY: "ply\n" or "ply\r"
        if (len >= 4 && buf[0] == 0x70 && buf[1] == 0x6C && buf[2] == 0x79
            && (buf[3] == 0x0A || buf[3] == 0x0D))
            return "PLY 3D Model";

        // ── Scientific / Medical ────────────────────────────────────────

        // FITS: "SIMPLE  = "
        if (len >= 10 && buf.AsSpan(0, 10).SequenceEqual("SIMPLE  = "u8))
            return "FITS Image";

        // NetCDF classic: "CDF" + version 1/2/5
        if (len >= 4 && buf[0] == 0x43 && buf[1] == 0x44 && buf[2] == 0x46
            && buf[3] is 0x01 or 0x02 or 0x05)
            return "NetCDF Data";

        // DICOM: "DICM" at offset 128
        if (len >= 132 && buf[128] == 0x44 && buf[129] == 0x49 && buf[130] == 0x43 && buf[131] == 0x4D)
            return "DICOM Medical Image";

        // NIfTI-1: "n+1\0" or "ni1\0" at offset 344
        if (len >= 348
            && ((buf[344] == 0x6E && buf[345] == 0x2B && buf[346] == 0x31 && buf[347] == 0x00)
                || (buf[344] == 0x6E && buf[345] == 0x69 && buf[346] == 0x31 && buf[347] == 0x00)))
            return "NIfTI Neuroimaging Data";

        // ── Geospatial ──────────────────────────────────────────────────

        // ESRI Shapefile: file code 9994 BE (00 00 27 0A)
        if (len >= 4 && buf[0] == 0x00 && buf[1] == 0x00 && buf[2] == 0x27 && buf[3] == 0x0A)
            return "ESRI Shapefile";

        // ── Network Captures ────────────────────────────────────────────

        // PCAP LE: D4 C3 B2 A1
        if (len >= 4 && buf[0] == 0xD4 && buf[1] == 0xC3 && buf[2] == 0xB2 && buf[3] == 0xA1)
            return "PCAP Capture";

        // PCAP BE: A1 B2 C3 D4
        if (len >= 4 && buf[0] == 0xA1 && buf[1] == 0xB2 && buf[2] == 0xC3 && buf[3] == 0xD4)
            return "PCAP Capture";

        // PCAPNG Section Header Block: 0A 0D 0D 0A
        if (len >= 4 && buf[0] == 0x0A && buf[1] == 0x0D && buf[2] == 0x0D && buf[3] == 0x0A)
            return "PCAPNG Capture";

        // ── Firmware / Embedded ─────────────────────────────────────────

        // U-Boot legacy image: 27 05 19 56
        if (len >= 4 && buf[0] == 0x27 && buf[1] == 0x05 && buf[2] == 0x19 && buf[3] == 0x56)
            return "U-Boot Image";

        // Flattened Device Tree Blob: D0 0D FE ED
        if (len >= 4 && buf[0] == 0xD0 && buf[1] == 0x0D && buf[2] == 0xFE && buf[3] == 0xED)
            return "Device Tree Blob";

        // Android Boot Image: "ANDROID!"
        if (len >= 8 && buf[0] == 0x41 && buf[1] == 0x4E && buf[2] == 0x44 && buf[3] == 0x52
            && buf[4] == 0x4F && buf[5] == 0x49 && buf[6] == 0x44 && buf[7] == 0x21)
            return "Android Boot Image";

        // ── Game ROMs ───────────────────────────────────────────────────

        // NES ROM (iNES): "NES\x1A"
        if (len >= 4 && buf[0] == 0x4E && buf[1] == 0x45 && buf[2] == 0x53 && buf[3] == 0x1A)
            return "NES ROM";

        // N64 ROM big-endian (z64): 80 37 12 40
        if (len >= 4 && buf[0] == 0x80 && buf[1] == 0x37 && buf[2] == 0x12 && buf[3] == 0x40)
            return "N64 ROM";

        // N64 ROM byte-swapped (v64): 37 80 40 12
        if (len >= 4 && buf[0] == 0x37 && buf[1] == 0x80 && buf[2] == 0x40 && buf[3] == 0x12)
            return "N64 ROM";

        // N64 ROM little-endian (n64): 40 12 37 80
        if (len >= 4 && buf[0] == 0x40 && buf[1] == 0x12 && buf[2] == 0x37 && buf[3] == 0x80)
            return "N64 ROM";

        // ── Deep-offset ROM detection (offset 0x100+) ──────────────────

        // Sega Genesis ROM: "SEGA" at offset 0x100
        if (len >= 260 && buf[0x100] == 0x53 && buf[0x101] == 0x45 && buf[0x102] == 0x47 && buf[0x103] == 0x41)
            return "Sega Genesis ROM";

        // Nintendo 3DS ROM (NCSD): "NCSD" at offset 0x100
        if (len >= 260 && buf[0x100] == 0x4E && buf[0x101] == 0x43 && buf[0x102] == 0x53 && buf[0x103] == 0x44)
            return "Nintendo 3DS ROM";

        // Game Boy ROM: Nintendo logo CE ED 66 66 at offset 0x104
        if (len >= 264 && buf[0x104] == 0xCE && buf[0x105] == 0xED && buf[0x106] == 0x66 && buf[0x107] == 0x66)
            return "Game Boy ROM";

        // ── Executables / Bytecode ──────────────────────────────────────

        // WASM: \0asm
        if (len >= 4 && buf[0] == 0x00 && buf[1] == 0x61 && buf[2] == 0x73 && buf[3] == 0x6D)
            return "WebAssembly Module";

        // ELF: 7F ELF
        if (len >= 4 && buf[0] == 0x7F && buf[1] == 0x45 && buf[2] == 0x4C && buf[3] == 0x46)
            return "ELF Executable";

        // Java Class: CA FE BA BE
        if (len >= 4 && buf[0] == 0xCA && buf[1] == 0xFE && buf[2] == 0xBA && buf[3] == 0xBE)
            return "Java Class File";

        // Java Serialized Object: AC ED 00 05
        if (len >= 4 && buf[0] == 0xAC && buf[1] == 0xED && buf[2] == 0x00 && buf[3] == 0x05)
            return "Java Serialized Object";

        // Dalvik Executable: "dex\n"
        if (len >= 4 && buf[0] == 0x64 && buf[1] == 0x65 && buf[2] == 0x78 && buf[3] == 0x0A)
            return "Dalvik Executable";

        // Windows PDB (MSF 7.0): "Microsoft C/C++ MSF 7.00\r\n\x1ADS"
        if (len >= 32 && buf.AsSpan(0, 29).SequenceEqual("Microsoft C/C++ MSF 7.00\r\n\x1ADS"u8))
            return "Windows PDB Debug Symbols";

        // Portable PDB (.NET): BSJB metadata signature at offset 0
        if (len >= 4 && buf[0] == 0x42 && buf[1] == 0x53 && buf[2] == 0x4A && buf[3] == 0x42)
            return "Portable PDB Debug Symbols";

        // Mach-O: 4 variants (32/64 bit, LE/BE)
        if (len >= 4)
        {
            uint magic = (uint)(buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3]);
            if (magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE)
                return "Mach-O Binary";
        }

        // Lua bytecode: \x1BLua
        if (len >= 4 && buf[0] == 0x1B && buf[1] == 0x4C && buf[2] == 0x75 && buf[3] == 0x61)
            return "Lua Bytecode";

        // ── System Files ────────────────────────────────────────────────

        // Windows Shortcut (.lnk): 4C 00 00 00 01 14 02 00
        if (len >= 8 && buf[0] == 0x4C && buf[1] == 0x00 && buf[2] == 0x00 && buf[3] == 0x00
            && buf[4] == 0x01 && buf[5] == 0x14 && buf[6] == 0x02 && buf[7] == 0x00)
            return "Windows Shortcut";

        // Windows Registry Hive: "regf"
        if (len >= 4 && buf[0] == 0x72 && buf[1] == 0x65 && buf[2] == 0x67 && buf[3] == 0x66)
            return "Windows Registry Hive";

        // Windows Event Log (EVTX): "ElfFile\0"
        if (len >= 8 && buf[0] == 0x45 && buf[1] == 0x6C && buf[2] == 0x66 && buf[3] == 0x46
            && buf[4] == 0x69 && buf[5] == 0x6C && buf[6] == 0x65 && buf[7] == 0x00)
            return "Windows Event Log";

        // Apple Binary Property List: "bplist"
        if (len >= 6 && buf[0] == 0x62 && buf[1] == 0x70 && buf[2] == 0x6C && buf[3] == 0x69
            && buf[4] == 0x73 && buf[5] == 0x74)
            return "Apple Binary Property List";

        // Git Pack: "PACK" + version 2/3
        if (len >= 8 && buf[0] == 0x50 && buf[1] == 0x41 && buf[2] == 0x43 && buf[3] == 0x4B
            && buf[4] == 0x00 && buf[5] == 0x00 && buf[6] == 0x00 && (buf[7] == 0x02 || buf[7] == 0x03))
            return "Git Pack File";

        // Git Index: "DIRC"
        if (len >= 4 && buf[0] == 0x44 && buf[1] == 0x49 && buf[2] == 0x52 && buf[3] == 0x43)
            return "Git Index File";

        // ── Short-signature binary (2 bytes, checked late) ──────────────

        // PE/EXE: MZ
        if (len >= 2 && buf[0] == 0x4D && buf[1] == 0x5A)
            return "PE Executable";

        // BMP: BM
        if (len >= 2 && buf[0] == 0x42 && buf[1] == 0x4D)
            return "BMP Image";

        // ── Text-based (checked last, slower) ───────────────────────────

        if (len >= 4 && !buf.AsSpan(0, Math.Min(len, 256)).Contains((byte)0x00))
        {
            var text = Encoding.UTF8.GetString(buf, 0, len);
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);
            var result = MatchText(text);
            if (result is not null) return result;
        }

        return null;
    }

    private static string? MatchText(string text)
    {
        // ── Shebang scripts ────────────────────────────────────────────
        if (text.StartsWith("#!", StringComparison.Ordinal))
        {
            var firstLine = text.AsSpan(0, Math.Min(text.Length, 128));
            var newline = firstLine.IndexOfAny('\n', '\r');
            if (newline > 0) firstLine = firstLine.Slice(0, newline);

            if (firstLine.Contains("bash", StringComparison.Ordinal)
                || firstLine.Contains("/sh", StringComparison.Ordinal)
                || firstLine.Contains("/ash", StringComparison.Ordinal)
                || firstLine.Contains("/zsh", StringComparison.Ordinal)
                || firstLine.Contains("/dash", StringComparison.Ordinal)
                || firstLine.Contains("/fish", StringComparison.Ordinal))
                return "Shell Script";
            if (firstLine.Contains("python", StringComparison.Ordinal))
                return "Python Script";
            if (firstLine.Contains("node", StringComparison.Ordinal))
                return "Node.js Script";
            if (firstLine.Contains("ruby", StringComparison.Ordinal))
                return "Ruby Script";
            if (firstLine.Contains("perl", StringComparison.Ordinal))
                return "Perl Script";
            if (firstLine.Contains("php", StringComparison.Ordinal))
                return "PHP Script";
            return "Script";
        }

        // ── HTML (before generic XML) ──────────────────────────────────
        if (text.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase))
            return "HTML Document";
        {
            var trimmed = text.AsSpan().TrimStart();
            // skip optional XML declaration for HTML check
            if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                var end = trimmed.IndexOf('>');
                if (end > 0 && end + 1 < trimmed.Length)
                    trimmed = trimmed.Slice(end + 1).TrimStart();
            }
            if (trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                return "HTML Document";
        }

        // ── SVG ────────────────────────────────────────────────────────
        if (text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            return "SVG Image";

        // ── FictionBook ────────────────────────────────────────────────
        if (text.StartsWith("<FictionBook", StringComparison.OrdinalIgnoreCase))
            return "FictionBook eBook";

        // ── XML (after HTML/SVG/FictionBook) ───────────────────────────
        if (text.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            return "XML Document";

        // ── JSON ───────────────────────────────────────────────────────
        {
            var trimmed = text.AsSpan().TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '{')
            {
                if (trimmed.Length > 1 && (trimmed[1] == '"' || char.IsWhiteSpace(trimmed[1]) || trimmed[1] == '}'))
                    return "JSON Data";
            }
            if (trimmed.Length > 0 && trimmed[0] == '[')
            {
                if (trimmed.Length > 1 && (trimmed[1] == '{' || trimmed[1] == '"' || trimmed[1] == '['
                    || char.IsDigit(trimmed[1]) || char.IsWhiteSpace(trimmed[1]) || trimmed[1] == ']'))
                    return "JSON Data";
            }
        }

        // ── YAML ───────────────────────────────────────────────────────
        if (text.StartsWith("---", StringComparison.Ordinal))
        {
            if (text.Length == 3 || text[3] == '\n' || text[3] == '\r' || text[3] == ' ')
                return "YAML Document";
        }
        if (text.StartsWith("%YAML", StringComparison.Ordinal))
            return "YAML Document";

        // ── CSS ────────────────────────────────────────────────────────
        if (text.Contains("@charset", StringComparison.OrdinalIgnoreCase)
            || text.Contains("@import", StringComparison.OrdinalIgnoreCase)
            || text.Contains("@media", StringComparison.OrdinalIgnoreCase)
            || text.Contains("@font-face", StringComparison.OrdinalIgnoreCase)
            || text.Contains("@keyframes", StringComparison.OrdinalIgnoreCase)
            || text.Contains("@layer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("@supports", StringComparison.OrdinalIgnoreCase))
            return "CSS Stylesheet";
        {
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith(":root {", StringComparison.Ordinal)
                || trimmed.StartsWith("* {", StringComparison.Ordinal)
                || trimmed.StartsWith("body {", StringComparison.Ordinal)
                || trimmed.StartsWith("html {", StringComparison.Ordinal))
                return "CSS Stylesheet";
        }

        // ── CSV ────────────────────────────────────────────────────────
        {
            var lines = text.Split('\n', 6); // grab up to 5 lines
            if (lines.Length >= 4)
            {
                var commaCount = -1;
                var consistent = true;
                for (var i = 0; i < Math.Min(lines.Length, 5); i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var count = 0;
                    foreach (var c in lines[i])
                        if (c == ',') count++;
                    if (count < 2) { consistent = false; break; }
                    if (commaCount == -1) commaCount = count;
                    else if (count != commaCount) { consistent = false; break; }
                }
                if (consistent && commaCount >= 2)
                    return "CSV Data";
            }
        }

        // ── TOML ───────────────────────────────────────────────────────
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            // [word.word] or [[word]]
            if (t.Length >= 5 && t[0] == '[' && t[1] == '[' && t[^1] == ']' && t[^2] == ']')
                return "TOML Document";
            if (t.Length >= 5 && t[0] == '[' && t[^1] == ']' && t.Contains('.'))
                return "TOML Document";
        }

        // ── INI ────────────────────────────────────────────────────────
        {
            var trimmed = text.TrimStart();
            if (trimmed.Length >= 3 && trimmed[0] == '[')
            {
                var close = trimmed.IndexOf(']');
                if (close > 1)
                {
                    var afterClose = close + 1;
                    if (afterClose < trimmed.Length && (trimmed[afterClose] == '\n' || trimmed[afterClose] == '\r'))
                        return "INI Configuration";
                }
            }
        }

        // ── Dockerfile ─────────────────────────────────────────────────
        if (text.StartsWith("FROM ", StringComparison.Ordinal))
            return "Dockerfile";

        // ── Diff/Patch ─────────────────────────────────────────────────
        if (text.StartsWith("diff --git", StringComparison.Ordinal)
            || text.StartsWith("--- a/", StringComparison.Ordinal))
            return "Diff/Patch";

        // ── LaTeX ──────────────────────────────────────────────────────
        if (text.Contains("\\documentclass", StringComparison.Ordinal)
            || text.Contains("\\begin{document}", StringComparison.Ordinal))
            return "LaTeX Document";

        // ── PGP ────────────────────────────────────────────────────────
        if (text.StartsWith("-----BEGIN PGP", StringComparison.Ordinal))
            return "PGP Armored Data";

        // ── PEM (after PGP) ────────────────────────────────────────────
        if (text.StartsWith("-----BEGIN ", StringComparison.Ordinal))
            return "PEM Encoded Data";

        // ── BitTorrent ─────────────────────────────────────────────────
        if (text.StartsWith("d8:announce", StringComparison.Ordinal))
            return "BitTorrent Metainfo";

        return null;
    }
}
