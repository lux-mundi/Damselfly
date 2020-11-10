﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Damselfly.Core.ImageProcessing;
using Damselfly.Core.Models;
using Damselfly.Core.Services;
using Damselfly.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Damselfly.Web.Controllers
{
    public class ImageController : Controller
    {   
        [HttpGet("~/rawimage/{imageId}")]
        public async Task<IActionResult> Image(string imageId, CancellationToken cancel)
        {
            if (int.TryParse(imageId, out var id))
            {

                using var db = new ImageContext();

                var image = db.Images.Where(x => x.ImageId == id)
                                     .Include(x => x.Folder)
                                     .FirstOrDefault();

                if (image != null)
                {
                    var stream = new FileStream(image.FullPath, FileMode.Open);
                    var result = new FileStreamResult(stream, "image/jpeg");
                    result.FileDownloadName = image.FileName;
                    return result;
                }
            }

            return null;
        }

        [HttpGet("~/thumb/{thumbSize}/{imageId}")]
        public async Task<IActionResult> Thumb(string thumbSize, string imageId, CancellationToken cancel)
        {
            if (Enum.TryParse<ThumbSize>( thumbSize, true, out var size))
            {

                if (int.TryParse(imageId, out var id))
                {
                    using var db = new ImageContext();

                    var image = db.Images.Where(x => x.ImageId == id)
                                         .Include(x => x.Folder)
                                         .FirstOrDefault();

                    if (image != null)
                    {
                        var file = new FileInfo(image.FullPath);
                        var path = ThumbnailService.Instance.GetThumbPath(file, size);

                        if (! System.IO.File.Exists(path))
                            path = "/no-image.png";

                        var stream = new FileStream(path, FileMode.Open);
                        var result = new FileStreamResult(stream, "image/jpeg");
                        result.FileDownloadName = image.FileName;
                        return result;
                    }
                }
            }

            return null;
        }
    }
}