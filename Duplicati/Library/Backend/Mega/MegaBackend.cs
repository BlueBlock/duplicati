﻿//  Copyright (C) 2015, The Duplicati Team
//  http://www.duplicati.com, info@duplicati.com
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using CG.Web.MegaApiClient;
using Duplicati.Library.Common.IO;
using Duplicati.Library.Interface;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Duplicati.Library.Backend.Mega
{
    // ReSharper disable once UnusedMember.Global
    // This class is instantiated dynamically in the BackendLoader.
    public class MegaBackend: IBackend, IStreamingBackend
    {
        private readonly string m_username = null;
        private readonly string m_password = null;
        private List<NodeFileEntry> m_listcache;
        private INode m_rootFolder = null;
        private readonly string m_prefix = null;

        private MegaApiClient m_client;

        public MegaBackend()
        {
        }

        private MegaApiClient Client
        {
            get
            {
                try
                {
                    if (m_client != null) return m_client;

                    var cl = new MegaApiClient();
                    cl.Login(m_username, m_password);
                    m_client = cl;

                    return m_client;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }

        public MegaBackend(string url, Dictionary<string, string> options)
        {
            var uri = new Utility.Uri(url);

            if (options.ContainsKey("auth-username"))
                m_username = options["auth-username"];
            if (options.ContainsKey("auth-password"))
                m_password = options["auth-password"];

            if (!string.IsNullOrEmpty(uri.Username))
                m_username = uri.Username;
            if (!string.IsNullOrEmpty(uri.Password))
                m_password = uri.Password;

            if (string.IsNullOrEmpty(m_username))
                throw new UserInformationException(Strings.MegaBackend.NoUsernameError, "MegaNoUsername");
            if (string.IsNullOrEmpty(m_password))
                throw new UserInformationException(Strings.MegaBackend.NoPasswordError, "MegaNoPassword");

            m_prefix = uri.HostAndPath ?? "";
        }
        
        private void GetCurrentFolder(bool autocreate = false)
        {
            try
            {
                var parts = m_prefix.Split(new[] { "/" }, StringSplitOptions.RemoveEmptyEntries);
                var nodes = Client.GetNodes();
                INode parent = nodes.First(x => x.Type == NodeType.Root);

                foreach (var n in parts)
                {
                    var item = nodes.FirstOrDefault(
                        x => x.Name == n 
                             && x.Type == NodeType.Directory 
                             && x.ParentId == parent.Id 
                             && x.Type != NodeType.Trash 
                             && x.Type != NodeType.Inbox);

                    if (item == null)
                    {
                        if (!autocreate)
                        {
                            throw new FolderMissingException();
                        }

                        item = Client.CreateFolder(n, parent);
                    }

                    parent = item;
                }

                m_rootFolder = parent;

                if (m_rootFolder != null)
                {
                    ResetFileCache(nodes);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
        
        private INode CurrentFolder
        {
            get
            {
                if (m_rootFolder == null)
                {
                    GetCurrentFolder(false);
                }

                return m_rootFolder;
            }
        }

        private class NodeFileEntry
        {
            public IFileEntry FileEntry { get; set; }
            public INode INode { get; set; }
        }

        private void ResetFileCache(IEnumerable<INode> list = null)
        {
            if (m_rootFolder == null)
            {
                GetCurrentFolder(false);
            }
            else
            {
                IEnumerable<INode> nodes = list ?? Client.GetNodes().Where(x => x.Type == NodeType.File || x.Type == NodeType.Directory);

                m_listcache = GetNodeFileEntryList(nodes, m_rootFolder, null, false);
            }
        }

        private NodeFileEntry GetFileNode(string name)
        {
            if (m_listcache != null && m_listcache.Any(x => x.FileEntry.Name == name))
            {
                return m_listcache.First(x => x.FileEntry.Name == name);
            }

            ResetFileCache();

            if (m_listcache != null && m_listcache.Any(x => x.FileEntry.Name == name))
            {
                return m_listcache.First(x => x.FileEntry.Name == name);
            }
            
            throw new FileMissingException();
        }

        #region IStreamingBackend implementation

        public Task PutAsync(string remotename, string filename, CancellationToken cancelToken)
        {
            using (System.IO.FileStream fs = System.IO.File.OpenRead(filename))
            {
                return PutAsync(remotename, fs, cancelToken);
            }
        }

        public async Task PutAsync(string remotename, System.IO.Stream stream, CancellationToken cancelToken)
        {
            try
            {
                if (m_listcache == null)
                {
                    ResetFileCache();
                }

                var path = SystemIO.IO_OS.PathGetDirectoryName(remotename);
                var filename = SystemIO.IO_OS.PathGetFileName(remotename);
                var targetFolder = CreateFolders(m_rootFolder, path);

                try
                {
                    var newNode = await Client.UploadAsync(stream, filename, targetFolder, new Progress(), null, cancelToken);
                    if (m_listcache.Any(x => x.FileEntry.Name == remotename))
                    {
                        // replacing existing file
                        Delete(remotename);
                    }

                    FileEntry newFileEntry = new FileEntry(
                        string.IsNullOrEmpty(path) ? newNode.Name : $"{path}/{newNode.Name}",
                        newNode.Size,
                        newNode.ModificationDate ?? new DateTime(0),
                        newNode.ModificationDate ?? new DateTime(0));

                    m_listcache.Add(new NodeFileEntry {FileEntry = newFileEntry, INode = newNode});
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
            catch
            {
                m_listcache = null;
                throw;
            }
        }

        public void Get(string remotename, System.IO.Stream stream)
        {
            if (m_listcache == null)
            {
                ResetFileCache();
            }

            using (Stream s = Client.Download(GetFileNode(remotename).INode))
            {
                Library.Utility.Utility.CopyStream(s, stream);
            }
        }

        #endregion

        #region IBackend implementation
        
        public IEnumerable<IFileEntry> List()
        {
            if (m_listcache == null)
            {
                ResetFileCache();
            }

            return m_listcache.Select(item => item.FileEntry).ToList();
        }

        private List<NodeFileEntry> GetNodeFileEntryList(
            IEnumerable<INode> nodes, 
            INode directoryNode, 
            string currentPath = null, 
            bool includeThisDirectoryNodePath = true)
        {
            var path = string.Empty;

            if (includeThisDirectoryNodePath)
            {
                path = string.IsNullOrEmpty(currentPath) ? directoryNode.Name : $"{currentPath}/{directoryNode.Name}";
            }

            List<NodeFileEntry> items = new List<NodeFileEntry>();
            foreach (var n in nodes.Where(x => x.ParentId == directoryNode.Id).OrderByDescending(x => x.Name.Length))
            {
                switch (n.Type)
                {
                    case NodeType.File:
                    {
                        FileEntry newFileEntry = new FileEntry(
                            string.IsNullOrEmpty(path) ? n.Name : $"{path}/{n.Name}", 
                            n.Size, 
                            n.ModificationDate ?? new DateTime(0),
                            n.ModificationDate ?? new DateTime(0));
                        items.Add(new NodeFileEntry {FileEntry = newFileEntry, INode = n});
                        continue;
                    }
                    case NodeType.Directory:
                    {
                        List<NodeFileEntry> subFolderItems = GetNodeFileEntryList(nodes, n, path);
                        items.AddRange(subFolderItems);
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return items;
        }

        public INode CreateFolders(INode currentFolder, string path)
        {
            List<string> foldersToCreate = path.Replace(@"\", @"/").Split('/').Where(s => s != string.Empty).ToList();

            if (!foldersToCreate.Any())
            {
                return currentFolder;
            }
            
            INode workingFolder = currentFolder;
            foreach (string folderToCreate in foldersToCreate)
            {
                if (string.IsNullOrEmpty(folderToCreate)) // change to check if folder exists
                {
                    continue;
                }
                workingFolder = Client.CreateFolder(folderToCreate, workingFolder);
            }

            return workingFolder;
        }

        public void Get(string remotename, string filename)
        {
            using (System.IO.FileStream fs = System.IO.File.Create(filename))
            {
                Get(remotename, fs);
            }
        }

        public void Delete(string remotename)
        {
            try
            {
                if (m_listcache == null || m_listcache.All(x => x.FileEntry.Name != remotename))
                {
                    ResetFileCache();
                }

                if (m_listcache.Any(x => x.FileEntry.Name != remotename))
                {
                    throw new FileMissingException();
                }

                Client.Delete(m_listcache.First(x => x.FileEntry.Name == remotename).INode, false);

                m_listcache.RemoveAll(x => x.FileEntry.Name == remotename);
            }
            catch
            {
                m_listcache = null;
                throw;
            }
        }

        public void Test()
        {
            this.TestList();
        }

        public void CreateFolder()
        {
            GetCurrentFolder(true);
        }

        public string DisplayName
        {
            get
            {
                return Strings.MegaBackend.DisplayName;
            }
        }

        public string ProtocolKey
        {
            get
            {
                return "mega";
            }
        }

        public IList<ICommandLineArgument> SupportedCommands
        {
            get
            {
                return new List<ICommandLineArgument>(new ICommandLineArgument[] {
                    new CommandLineArgument("auth-password", CommandLineArgument.ArgumentType.Password, Strings.MegaBackend.AuthPasswordDescriptionShort, Strings.MegaBackend.AuthPasswordDescriptionLong),
                    new CommandLineArgument("auth-username", CommandLineArgument.ArgumentType.String, Strings.MegaBackend.AuthUsernameDescriptionShort, Strings.MegaBackend.AuthUsernameDescriptionLong),
                });
            }
        }

        public string Description
        {
            get
            {
                return Strings.MegaBackend.Description;
            }
        }

        public string[] DNSName
        {
            get { return null; }
        }

        #endregion

        #region IDisposable implementation

        public void Dispose()
        {
        }

        #endregion

        private class Progress : IProgress<double>
        {
            public void Report(double value)
            {
                // No implementation as we have already wrapped the stream in our own progress reporting stream
            }
        }
    }
}
