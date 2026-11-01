using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Collections.Generic;
using UnityEngine;
public static class MechReviewLogArchive
{
    [Serializable] public sealed class Entry {public string path,sha256,error;public long originalBytes,compressedBytes;}
    [Serializable] public sealed class Manifest {public Entry[] files;}
    static string Hash(Stream stream){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
    public static void CompressClosedRun(string directory)
    {
        string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var records=new List<Entry>();
        // Never traverse a junction into another task's artifacts.
        foreach(var file in Enumerate(new DirectoryInfo(root)))
        {
            if(!file.FullName.StartsWith(root,StringComparison.OrdinalIgnoreCase)||file.Extension!=".jsonl")continue;
            string target=file.FullName+".gz",temporary=target+".tmp";
            if(File.Exists(target)||File.Exists(temporary))continue;
            var record=new Entry{path=file.FullName,originalBytes=file.Length};records.Add(record);
            try
            {
                using(var source=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.None))
                {
                    record.sha256=Hash(source);source.Position=0;
                    using(var zip=new GZipStream(File.Create(temporary),System.IO.Compression.CompressionLevel.Fastest))source.CopyTo(zip);
                    using var check=new GZipStream(File.OpenRead(temporary),CompressionMode.Decompress);
                    if(Hash(check)!=record.sha256)throw new IOException("Archive hash mismatch; original retained.");
                }
                File.Move(temporary,target);record.compressedBytes=new FileInfo(target).Length;
                File.Delete(file.FullName);
            }
            catch(Exception ex){record.error=ex.Message;}
        }
        File.WriteAllText(Path.Combine(root,"log-archive.json"),JsonUtility.ToJson(new Manifest{files=records.ToArray()},true));
    }
    static IEnumerable<FileInfo> Enumerate(DirectoryInfo folder)
    {
        if((folder.Attributes&FileAttributes.ReparsePoint)!=0)yield break;
        foreach(var file in folder.GetFiles())if((file.Attributes&FileAttributes.ReparsePoint)==0)yield return file;
        foreach(var sub in folder.GetDirectories())foreach(var file in Enumerate(sub))yield return file;
    }
}
