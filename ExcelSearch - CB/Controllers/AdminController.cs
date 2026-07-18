using ExcelSearch___CB.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Mvc;



namespace ExcelSearch___CB.Controllers
{
    public class AdminController : Controller
    {
        // GET: Admin
        public ActionResult Index()
        {
            return View();
        }

        public ActionResult Overview()
        {
            var model = new OverviewViewModel
            {
                AdminName = "Administrator",
                AdminRole = "Full Access",

                FilesIndexed = 0,
                FilesIndexedDescription = "Across folders",

                TotalRecords = 0,
                TotalRecordsDescription = "Across all sheets",

                FailedFiles = 0,
                FailedFilesDescription = "No failed files",

                StorageUsed = 0,
                StorageLimit = 50,

                LastIndexTime = DateTime.Now,

                PipelineStages = new List<PipelineStageViewModel>
{
    new PipelineStageViewModel
    {
        StageName="Upload",
        Details="0 files",
        Status="Waiting"
    },

    new PipelineStageViewModel
    {
        StageName="Validate",
        Details="0 passed",
        Status="Waiting"
    },

    new PipelineStageViewModel
    {
        StageName="Store",
        Details="Database",
        Status="Ready"
    },

    new PipelineStageViewModel
    {
        StageName="Index",
        Details="Waiting",
        Status="Pending"
    },

    new PipelineStageViewModel
    {
        StageName="Ready",
        Details="0 / 0",
        Status="Complete"
    }
}
            };


            return View(model);
        }
        public ActionResult Upload()
        {
            var model = new UploadViewModel
            {
                AdminName = "Administrator",
                AdminRole = "Full Access",

                RecentUploads = new List<UploadedFileViewModel>
        {
            new UploadedFileViewModel
            {
                FileName = "Sample.xlsx",
                FileSize = "2.4 MB",
                RowCount = 78000,
                UploadedTime = "2 min ago",
                Status = "Indexed"
            },

            new UploadedFileViewModel
            {
                FileName = "Example.csv",
                FileSize = "1.8 MB",
                RowCount = 45000,
                UploadedTime = "5 min ago",
                Status = "Indexed"
            }
        }
            };


            return View(model);
        }


        [HttpPost]
        public ActionResult UploadFile(HttpPostedFileBase excelFile)
        {
            if (excelFile == null || excelFile.ContentLength == 0)
            {
                TempData["Message"] = "Please select a file.";
                return RedirectToAction("Upload");
            }

            string uploads = Server.MapPath("~/Uploads");

            if (!Directory.Exists(uploads))
                Directory.CreateDirectory(uploads);

            string fileName = Path.GetFileName(excelFile.FileName);
            string path = Path.Combine(uploads, fileName);

            System.Diagnostics.Debug.WriteLine("Saving to: " + path);
            excelFile.SaveAs(path);

            TempData["Message"] = "File uploaded successfully.";

            return RedirectToAction("Upload");
        }

        public ActionResult IndexMonitor()
        {
            var model = new IndexMonitorViewModel
            {
                AdminName = "Administrator",
                AdminRole = "Full Access",

                QueueFiles = 28,
                ProcessingFiles = 1,
                CompletedFiles = 12,
                FailedFiles = 0,

                CurrentFile = "Building.xlsx",
                CurrentWorksheet = "Units",

                RowsProcessed = 34000,
                TotalRows = 78000,

                CompletionPercentage = 65,

                EstimatedTime = "~4 minutes",


                QueueItems = new List<IndexQueueItemViewModel>
        {
            new IndexQueueItemViewModel
            {
                FileName="Building.xlsx",
                Status="Processing"
            },

            new IndexQueueItemViewModel
            {
                FileName="Palm.xlsx",
                Status="Queued"
            }
        },


                Activities = new List<IndexActivityViewModel>
        {
            new IndexActivityViewModel
            {
                Message="Indexing started",
                Time="2 minutes ago"
            },

            new IndexActivityViewModel
            {
                Message="File validation completed",
                Time="5 minutes ago"
            }
        }

            };


            return View(model);
        }

        [HttpPost]
        public ActionResult StartIndexing(HttpPostedFileBase excelFile)
        {
            if (excelFile == null || excelFile.ContentLength == 0)
            {
                TempData["Message"] = "Please select an Excel or CSV file.";
                return RedirectToAction("Upload");
            }

            string uploads = Server.MapPath("~/Uploads");

            if (!Directory.Exists(uploads))
                Directory.CreateDirectory(uploads);

            string fileName = Path.GetFileName(excelFile.FileName);
            string path = Path.Combine(uploads, fileName);

            excelFile.SaveAs(path);

            TempData["Message"] = "File uploaded successfully. Indexing started.";

            return RedirectToAction("IndexMonitor");
        }

        //[HttpPost]
        //public JsonResult StartIndexing(HttpPostedFileBase excelFile)
        //{
        //    if (excelFile == null || excelFile.ContentLength == 0)
        //    {
        //        return Json(new
        //        {
        //            success = false,
        //            message = "Please select a file."
        //        });
        //    }

        //    // Save file
        //    // Start indexing

        //    return Json(new
        //    {
        //        success = true,
        //        message = "File uploaded successfully. Indexing started."
        //    });
        //}


        public ActionResult AllFiles()
        {
            string folder = Server.MapPath("~/Uploads");


            var files = new List<FileItemViewModel>();


            if (Directory.Exists(folder))
            {

                var uploadedFiles = Directory.GetFiles(folder);


                foreach (var file in uploadedFiles)
                {

                    FileInfo info = new FileInfo(file);


                    files.Add(new FileItemViewModel
                    {
                        FileName = info.Name,

                        FileSize = FormatSize(info.Length),

                        LastIndexed = info.LastWriteTime.ToString("dd MMM yyyy, HH:mm"),

                        Records = 0,

                        Status = "Uploaded"
                    });

                }

            }



            var model = new AllFilesViewModel
            {

                AdminName = "Administrator",

                AdminRole = "Full Access",

                TotalFiles = files.Count,

                SummaryText = "Files currently stored in upload folder",

                Files = files

            };


            return View(model);
        }



        private string FormatSize(long bytes)
        {
            if (bytes > 1024 * 1024)
                return Math.Round(bytes / 1024f / 1024f, 2) + " MB";


            return Math.Round(bytes / 1024f, 2) + " KB";
        }


        public ActionResult FailedFiles()
        {
            var model = new FailedFilesViewModel
            {
                AdminName = "Administrator",
                AdminRole = "Full Access",

                TotalFailedFiles = 0,

                Description = "Files that could not be indexed.",

                Files = new List<FailedFileItemViewModel>()
            };

            return View(model);
        }


        public ActionResult SearchHistory()
        {
            var model = new SearchHistoryViewModel
            {
                AdminName = "Administrator",
                AdminRole = "Full Acce ss",

                TotalSearches = 0,
                TodaySearches = 0,
                AverageResults = 0,
                MostPopularSearch = "-",

                Searches = new List<SearchHistoryItemViewModel>()
            };

            return View(model);
        }


        public ActionResult ExportHistory()
        {
            var model = new ExportHistoryViewModel
            {
                AdminName = "Administrator",
                AdminRole = "Full Access",

                TotalExports = 0,
                TodayExports = 0,
                WeeklyExports = 0,
                TotalSize = "0 MB",

                Exports = new List<ExportHistoryItemViewModel>()
            };

            return View(model);
        }

        public ActionResult Storage()
        {
            string folderPath = Server.MapPath("~/Uploads");

            var filesList = new List<StorageFileViewModel>();

            long totalBytes = 0;


            if (Directory.Exists(folderPath))
            {
                var files = Directory.GetFiles(folderPath);


                foreach (var file in files)
                {
                    FileInfo info = new FileInfo(file);


                    totalBytes += info.Length;


                    filesList.Add(new StorageFileViewModel
                    {
                        FileName = info.Name,
                        FileSize = FormatSize(info.Length)
                    });

                }


                // largest files first
                filesList = filesList
                            .OrderByDescending(x =>
                            ConvertSizeToBytes(x.FileSize))
                            .Take(5)
                            .ToList();
            }



            // Temporary storage limit (50 GB)
            double storageLimitGB = 50;


            double usedGB = totalBytes /
                            (1024 * 1024 * 1024);


            int percentage = 0;


            if (storageLimitGB > 0)
            {
                percentage = (int)((usedGB / storageLimitGB) * 100);
            }



            var model = new StorageViewModel
            {
                AdminName = "Administrator",
                AdminRole = "Full Access",


                StorageUsed = Math.Round(usedGB, 2) + " GB",

                StorageLimit = storageLimitGB + " GB",


                StoragePercentage = percentage,


                TotalFiles = filesList.Count,


                DatabaseSize = "Not Connected",


                TotalRecords = "Not Indexed",


                LastBackup = "Not Available",


                Files = filesList
            };


            return View(model);
        }


        private long ConvertSizeToBytes(string size)
        {
            if (string.IsNullOrEmpty(size))
                return 0;


            double value;


            if (size.Contains("MB"))
            {
                value = double.Parse(size.Replace("MB", "").Trim());

                return (long)(value * 1024 * 1024);
            }


            if (size.Contains("KB"))
            {
                value = double.Parse(size.Replace("KB", "").Trim());

                return (long)(value * 1024);
            }


            return 0;
        }






    }




}