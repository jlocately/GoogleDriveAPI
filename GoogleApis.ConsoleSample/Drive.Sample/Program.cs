﻿/*
Copyright 2013 Google Inc

Licensed under the Apache License, Version 2.0(the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/


using System;
using System.Collections.Generic;

using System.Threading;
using System.Threading.Tasks;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Download;
using Google.Apis.Drive.v2;
using Google.Apis.Drive.v2.Data;
using Google.Apis.Logging;
using Google.Apis.Services;
using Google.Apis.Upload;


namespace Drive.Sample
{
    /// <summary>
    /// A sample for the Drive API. This samples demonstrates resumable media upload and media download.
    /// See https://developers.google.com/drive/ for more details regarding the Drive API.
    /// </summary>
    class Program
    {
        static Program()
        {
            // initialize the log instance
            ApplicationContext.RegisterLogger(new Log4NetLogger());
            Logger = ApplicationContext.Logger.ForType<ResumableUpload<Program>>();
        }

        #region Consts

        private const int KB = 0x400;
        private const int DownloadChunkSize = 256 * KB;

        // CHANGE THIS with full path to the file you want to upload
        private const string UploadFileName = @"Desert.jpg";

        // CHANGE THIS with a download directory
        private const string DownloadDirectoryName = @"C:\Users\Public\Pictures\Sample Pictures";

        // CHANGE THIS if you upload a file type other than a jpg
        private const string ContentType = @"image/jpeg";

        //Path name on Google Drive to insert the file
        private const string PathName = @"LIVROS";

        /// <summary>The logger instance.</summary>
        private static readonly ILogger Logger;

        /// <summary>The Drive API scopes.</summary>
        private static readonly string[] Scopes = new[] { DriveService.Scope.DriveFile, DriveService.Scope.Drive };

        /// <summary>
        /// The file which was uploaded. We will use its download Url to download it using our media downloader object.
        /// </summary>
        private static File uploadedFile;

        #endregion

        static void Main(string[] args)
        {
            Console.WriteLine("Google Drive API Sample");

            try
            {
                new Program().Run().Wait();
            }
            catch (AggregateException ex)
            {
                foreach (var e in ex.InnerExceptions)
                {
                    Console.WriteLine("ERROR: " + e.Message);
                }
            }

            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
        }

        private async Task Run()
        {
            GoogleWebAuthorizationBroker.Folder = "Drive.Sample";
            UserCredential credential;
            using (var stream = new System.IO.FileStream("client_secrets.json",
                System.IO.FileMode.Open, System.IO.FileAccess.Read))
            {
                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets, Scopes, "user", CancellationToken.None);
            }

            // Create the service.
            var service = new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "Drive API Sample",
            });
            await UploadFileAsync(service);

            // uploaded succeeded
            Console.WriteLine("\"{0}\" was uploaded successfully", uploadedFile.Title);
            //await DownloadFile(service, uploadedFile.DownloadUrl);
            //await DeleteFile(service, uploadedFile);
        }

        /// <summary>Uploads file asynchronously.</summary>
        private Task<IUploadProgress> UploadFileAsync(DriveService service)
        {
            #region Preenche Metadados
            File body = new File();
            body.MimeType = ContentType;
            body.Title = UploadFileName;
            if (body.Title.LastIndexOf('\\') != -1)
            {
                body.Title = body.Title.Substring(body.Title.LastIndexOf('\\') + 1);
            }

            List<File> result = new List<File>();
            FilesResource.ListRequest requestList = service.Files.List();
            const string FILTRO = "mimeType = 'application/vnd.google-apps.folder' and title = '{0}'";
            requestList.Q = String.Format(FILTRO, PathName);
           
            FileList files = requestList.Execute();

            //Google.Apis.Drive.v2.Data.File arquivosContato = files.Items.FirstOrDefault(t => t.Title.ToUpper() == nomePasta.ToUpper());

            string parentId = files.Items[0].Id;
            // Set the parent folder.
            if (!String.IsNullOrEmpty(parentId))
            {
                body.Parents = new List<ParentReference>() { new ParentReference() { Id = parentId } };
            }
            #endregion 
           
            var uploadStream = new System.IO.FileStream(UploadFileName, System.IO.FileMode.Open,
                System.IO.FileAccess.Read);

            var insert = service.Files.Insert(body, uploadStream, ContentType);

            insert.ChunkSize = FilesResource.InsertMediaUpload.MinimumChunkSize * 2;
            insert.ProgressChanged += Upload_ProgressChanged;
            insert.ResponseReceived += Upload_ResponseReceived;

            var task = insert.UploadAsync();

            task.ContinueWith(t =>
            {
                // NotOnRanToCompletion - this code will be called if the upload fails
                Console.WriteLine("Upload Filed. " + t.Exception);
            }, TaskContinuationOptions.NotOnRanToCompletion);
            task.ContinueWith(t =>
            {
                Logger.Debug("Closing the stream");
                uploadStream.Dispose();
                Logger.Debug("The stream was closed");
            });

            return task;
        }

        /// <summary>Downloads the media from the given URL.</summary>
        private async Task DownloadFile(DriveService service, string url)
        {
            var downloader = new MediaDownloader(service);
            downloader.ChunkSize = DownloadChunkSize;
            // add a delegate for the progress changed event for writing to console on changes
            downloader.ProgressChanged += Download_ProgressChanged;

            // figure out the right file type base on UploadFileName extension
            var lastDot = UploadFileName.LastIndexOf('.');
            var fileName = DownloadDirectoryName + @"\Download" +
                (lastDot != -1 ? "." + UploadFileName.Substring(lastDot + 1) : "");
            using (var fileStream = new System.IO.FileStream(fileName,
                System.IO.FileMode.Create, System.IO.FileAccess.Write))
            {
                var progress = await downloader.DownloadAsync(url, fileStream);
                if (progress.Status == DownloadStatus.Completed)
                {
                    Console.WriteLine(fileName + " was downloaded successfully");
                }
                else
                {
                    Console.WriteLine("Download {0} was interpreted in the middle. Only {1} were downloaded. ",
                        fileName, progress.BytesDownloaded);
                }
            }
        }

        /// <summary>Deletes the given file from drive (not the file system).</summary>
        private async Task DeleteFile(DriveService service, File file)
        {
            Console.WriteLine("Deleting file '{0}'...", file.Id);
            await service.Files.Delete(file.Id).ExecuteAsync();
            Console.WriteLine("File was deleted successfully");
        }

        #region Progress and Response changes

        static void Download_ProgressChanged(IDownloadProgress progress)
        {
            Console.WriteLine(progress.Status + " " + progress.BytesDownloaded);
        }

        static void Upload_ProgressChanged(IUploadProgress progress)
        {
            Console.WriteLine(progress.Status + " " + progress.BytesSent);
        }

        static void Upload_ResponseReceived(File file)
        {
            uploadedFile = file;
        }

        #endregion
    }
}
