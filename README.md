![Damselfly Screenshot](Damselfly.Web/wwwroot/damselfly-logo.png)

# Damselfly
Damselfly is a server-based Digital Asset Management system. The goal of Damselfly is to index an extremely large collection of images, and allow easy search and retrieval of those images, using metadata such as the IPTC keyword tags, as well as the folder and file names. 

![Damselfly Screenshot](docs/Screenshot.png)

## Features

* Server-based deployment, with a web UI, so the image library can be accessed via multiple devices without having to copy catalogues or other DBs to local device storage. 
* Focus on extremely fast performance - searching a 500,000-image catalogue returns results in around a second.
* Full-text search with multi-phrase partial-word searches
* Fast keyword tagging workflow addition, with non-destructive tagging (JPEG images are not re-encoded)
* Background indexing of images, so that the collection is automatically updated when new images are added quickly
* Background thumbnail generation
* Synology DSM/Photos compatibility mode to re-use the same thumbnails as your NAS 
* Selection basket for collecting images from search results to save locally and work within Digikam/PhotoShop/etc.
* Download/export processing to watermark images ready for social media, or sending via Email etc.
* Runs on Windows, Linux and OSX, and in Docker.
* Desktop Client for hosted site to allow closer native integration with client OS
* Direct upload to Wordpress 
* Persistable named basket selections

## Planned Features/Enhancements

* Direct upload to CMS platforms
* Direct sharing to social media (Twitter, Facebook etc)
* Support for selection and upload to Alamy Stock Image photo service
* Simple editing/manipulation

## How do we use Damselfly? What's the workflow?

Workflow is a little like distributed software development using Git. Photo manipulation is done locally, but then the images can be pushed to the remote server for integration into the main collection, and deleted from local storage. When you want to edit specific photos you can search and export/download them back to your local work area.

### Suggested workflow.

1. Images are copied onto a laptop for initial sorting, quality checks, and IPTC tagging using Picasa or Digikam
2. [Rclone](www.rclone.org) script syncs the new images across the LAN to the network share
3. Damselfly automatically picks up the new images and indexes them (and generates thumbnails) within 30 minutes
4. Images are now searchable in Damselfly and can be added to the Damselfly 'basket' with a single click
5. Images in the basket can be copied back to the desktop/laptop for local editing in Lightroom/On1/Digikam/etc.
   * Use the Damselfly Desktop client to write the files directly to the local filesystem in the same structure as on the server.
   * Export to a zip file to download and extract into the local image folder for additional editing
6. Re-sync using RClone to push the images back to the collection.

## Why 'Damselfly'?

Etymology of the name: DAM-_sel_-fly - **D**igital **A**sset **M**anagement that flies.

## Why Damsefly?

My wife is a horticultural writer and photographer ([pumpkinbeth.com](http://www.pumpkinbeth.com)) and over the years has accumulated a horticultural photography library with more than half a million pictures, in excess of 3TB of space. 

In order to find and retrieve photographs efficiently when writing about a particular subject, all of the photos are meticulously tagged with IPTC keywords describing the content and subject matter of each image. However, finding a digital asset management system that supports that volume of images is challenging. We've considered many software packages, and each has problems:

* Lightroom
  * Pro: Excellent keyword tagging support
  * Pro: Fast when used with local images
  * Con: Catalogues are not server-based, which means that images can only be searched from one laptop or device.
  * Con: Catalogue performance is terrible with more than about 50,000 images - which means multiple catalogues, or terrible performance
  * Con: Importing new images across the LAN (when the catalogue is based on a NAS or similar) is slow.
  * Con: Imports are not incremental by date, which means that to add new photos, the entire 3TB collection must be read across the LAN
  * Con: Lightroom 6 is 32-bit only, so not supported on OSX Catalina
* Picasa
  * Pro: Simple UI and workflow
  * Pro: Very fast and efficient IPTC keyword tagging
  * Con: Doesn't support network shares properly
  * Con: Can't handle more than about 15,000 images before it starts to behave erratically
  * Con: No longer supported by Google, and 32-bit only, so no OSX Catalina support
* ON1 RAW
  * Pro: Simple UI and workflow
  * Pro: Fast cataloging/indexing of local photos
  * Pro: Not too expensive
  * Con: Slow to index across a network share
* FileRun
  * Pro: Great search support
  * Con: Really designed for documents, rather than specifically for image management
  * Con: Can support server-side indexing and shared multi-device catalogues - but Windows-only
* ACDSee
  * Pro: Fast, 
  * Con: The 'non-destructive' workflow is, actually, destructive, and can easily result in loss of images.
* Digikam
  * Pro: Free/OSS
  * Pro: Excellent for working with a local collection
  * Con: Performance is terrible for collections > 50k images, whether using Sqlite or MySql/MariaDB. 
  * Con: Startup takes > 10 minutes on OSX with 100k+ images.
* Google Photos 
  * Pro: Excellent for large collections
  * Pro: Image recognition technology can help with searching
  * Con: Search ignores IPTC tags
  * Con: Expensive for > 1TB storage
* Amazon Cloud Drive
  * Pro: Excellent for large collections
  * Pro: Unlimited Storage of images included free with Prime
  * Pro: Image recognition technology can help with searching
  * Con: Search ignores IPTC tags
  * Con: Only supports Amazon's native apps. No support for _any_ third party clients.

## Damelfly Architecture

Damselfly is written using C#/.Net Core and Blazor Server. The data model and DB access is using Entity Framework Core. Currently the server supports Sqlite, but a future enhancement may be to add support for PostGres, MySql or MariaDB.

## Wordpress Integration

Damselfly allows direct uploads of photographs to the media library of a Wordpress Blog. To enable this feature, you must configure your Wordpress site to support JWT authentication. For more details see [JWT Authentication for WP REST API](https://wordpress.org/plugins/jwt-authentication-for-wp-rest-api/).

To enable this option you’ll need to edit your .htaccess file adding the following:

    RewriteEngine on
    RewriteCond %{HTTP:Authorization} ^(.*)
    RewriteRule ^(.*) - [E=HTTP_AUTHORIZATION:%1]
    SetEnvIf Authorization "(.*)" HTTP_AUTHORIZATION=$1
    
The JWT needs a secret key to sign the token this secret key must be unique and never revealed. To add the secret key edit your wp-config.php file and add a new constant called JWT_AUTH_SECRET_KEY

    define('JWT_AUTH_SECRET_KEY', 'your-top-secret-key');

To enable the CORs Support edit your wp-config.php file and add a new constant called JWT_AUTH_CORS_ENABLE

    define('JWT_AUTH_CORS_ENABLE', true);

You can use a string from [here](https://api.wordpress.org/secret-key/1.1/salt/).

Once you have the site configured:

1. Install the [Wordpress JWT Authentication for WP REST API](https://wordpress.org/plugins/jwt-authentication-for-wp-rest-api/) plugin.
2. Use the config page in Damselfly to set the website URL, username and password. I recommend setting up a dedicated user account for Damselfly to use.

## Contributing to Damselfly

I am a professional developer, but Damselfly is a side-project. I'm also not a web designer or CSS expert (by any means). If you'd like to contribute to Damselfly with features, enhancements, or with some proper shiny design/layout enhancements, please submit a PR!

## Thanks and Credits

* Microsoft [Blazor.Net](https://blazor.net)
* [SkiaSharp](https://github.com/mono/SkiaSharp) Fast library for Thumbnail generation
* [SixLabors ImageSharp](https://github.com/SixLabors/ImageSharp) Portable library for Thumbnail generation
* Drew Noakes' [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet), for IPTC and other image meta-data indexing
* IPTC Tag management using [ExifTool](https://exiftool.org/)
* Icons by [Font Awesome](https://fontawesome.com/)
* Chris Sainty for [Blazored](https://github.com/Blazored) Modal and Typeahead, and all his excellent info on Blazor
* [Serilog.Net](https://serilog.net/) for logging
* Wisne for [Infinite Scroll](https://github.com/wisne/InfiniteScroll-BlazorServer) inspiration
* SamProf for the [Virtual Scrolling](https://github.com/SamProf/BlazorVirtualScrolling) proof-of-concept