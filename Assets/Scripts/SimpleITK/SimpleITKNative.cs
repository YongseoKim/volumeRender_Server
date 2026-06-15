using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using itk.simple;

namespace SimpleITKUnity
{
    public struct SimpleITKImageInfo
    {
        public int Dimension;
        public ulong[] Size;
        public double[] Spacing;
        public double[] Origin;
        public double[] Direction;
        public int PixelId;
        public int ComponentsPerPixel;

        public override string ToString()
        {
            return $"Dim={Dimension}, Size=[{string.Join(", ", Size)}], " +
                   $"Spacing=[{string.Join(", ", Spacing)}], Origin=[{string.Join(", ", Origin)}], " +
                   $"Direction=[{string.Join(", ", Direction ?? Array.Empty<double>())}], " +
                   $"PixelId={PixelId}, Components={ComponentsPerPixel}";
        }
    }

    public struct SimpleITKVolumeData
    {
        public int Width;
        public int Height;
        public int Depth;
        public float[] VoxelsNormalized;
        public SimpleITKImageInfo ImageInfo;
        public float SourceMinimum;
        public float SourceMaximum;

        /// <summary>
        /// Call immediately after uploading to a GPU texture to clear the managed
        /// float[] reference and allow earlier garbage collection.
        /// </summary>
        public void ConsumeVoxels()
        {
            VoxelsNormalized = null;
        }
    }

    public struct SimpleITKScalarVolumeData
    {
        public int Width;
        public int Height;
        public int Depth;
        public float[] Voxels;
        public SimpleITKImageInfo ImageInfo;
        public float SourceMinimum;
        public float SourceMaximum;

        public void ConsumeVoxels()
        {
            Voxels = null;
        }
    }

    public struct SimpleITKLabelVolumeData
    {
        public int Width;
        public int Height;
        public int Depth;
        public byte[] Voxels;
        public SimpleITKImageInfo ImageInfo;
        public byte LabelMaximum;

        public void ConsumeVoxels()
        {
            Voxels = null;
        }
    }

    [Serializable]
    public struct SimpleITKVolumeProcessingOptions
    {
        public bool EnablePreGaussianSmoothing;
        public double PreGaussianSigma;
        public bool EnablePreMedianFilter;
        public uint PreMedianRadius;
        public bool EnablePreBilateralFilter;
        public double PreBilateralDomainSigma;
        public double PreBilateralRangeSigma;
        public uint PreBilateralRangeGaussianSamples;

        public bool EnablePostAdaptiveHistogramEqualization;
        public uint PostAdaptiveHistogramRadius;
        public double PostAdaptiveHistogramAlpha;
        public double PostAdaptiveHistogramBeta;

        public static SimpleITKVolumeProcessingOptions Disabled => new SimpleITKVolumeProcessingOptions
        {
            EnablePreGaussianSmoothing = false,
            PreGaussianSigma = 0.8,
            EnablePreMedianFilter = false,
            PreMedianRadius = 1,
            EnablePreBilateralFilter = false,
            PreBilateralDomainSigma = 2.0,
            PreBilateralRangeSigma = 0.08,
            PreBilateralRangeGaussianSamples = 32,
            EnablePostAdaptiveHistogramEqualization = false,
            PostAdaptiveHistogramRadius = 2,
            PostAdaptiveHistogramAlpha = 0.3,
            PostAdaptiveHistogramBeta = 0.3
        };
    }

    public static class SimpleITKNative
    {
        public static bool TryGetVersion(out string version, out string error)
        {
            version = string.Empty;
            error = string.Empty;

            try
            {
                version = itk.simple.Version.VersionString();
                return true;
            }
            catch (Exception ex)
            {
                error = BuildPluginLoadError(ex);
                return false;
            }
        }

        public static bool TryReadImageInfo(string imagePath, out SimpleITKImageInfo info, out string error)
        {
            info = default;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "imagePath must not be empty.";
                return false;
            }

            try
            {
                if (!TryReadImageWithDicomSupport(imagePath, out var image, out error))
                {
                    return false;
                }

                using (image)
                {
                    info = ConvertToManaged(image);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = BuildPluginLoadError(ex);
                return false;
            }
        }

        public static bool TryComputeMinMax(string imagePath, out double minimum, out double maximum, out string error)
        {
            minimum = 0.0;
            maximum = 0.0;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "imagePath must not be empty.";
                return false;
            }

            try
            {
                if (!TryReadImageWithDicomSupport(imagePath, out var image, out error))
                {
                    return false;
                }

                using (image)
                {
                    if (image.GetNumberOfComponentsPerPixel() != 1)
                    {
                        error = "ComputeMinMax supports scalar images only.";
                        return false;
                    }

                    using var statisticsFilter = new StatisticsImageFilter();
                    statisticsFilter.Execute(image);
                    minimum = statisticsFilter.GetMinimum();
                    maximum = statisticsFilter.GetMaximum();
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = BuildPluginLoadError(ex);
                return false;
            }
        }

        public static bool TryReadVolumeFloat32Normalized(
            string imagePath,
            out SimpleITKVolumeData volumeData,
            out string error)
        {
            return TryReadVolumeFloat32NormalizedProcessed(
                imagePath,
                SimpleITKVolumeProcessingOptions.Disabled,
                out volumeData,
                out error);
        }

        /// <summary>
        /// Reads volume data on a background thread and returns it as a Task.
        /// The operation can be cancelled via <see cref="CancellationToken"/>.
        /// Returns (true, volumeData, string.Empty) on success and (false, default, errorMessage) on failure.
        /// </summary>
        public static Task<(bool Success, SimpleITKVolumeData VolumeData, string Error)>
            TryReadVolumeFloat32NormalizedAsync(
                string imagePath,
                SimpleITKVolumeProcessingOptions processingOptions,
                CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var success = TryReadVolumeFloat32NormalizedProcessed(
                    imagePath,
                    processingOptions,
                    out var volumeData,
                    out var error);

                cancellationToken.ThrowIfCancellationRequested();
                return (success, volumeData, error);
            }, cancellationToken);
        }

        public static bool TryReadVolumeFloat32NormalizedProcessed(
            string imagePath,
            SimpleITKVolumeProcessingOptions processingOptions,
            out SimpleITKVolumeData volumeData,
            out string error)
        {
            volumeData = default;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "imagePath must not be empty.";
                return false;
            }

            try
            {
                if (!TryReadImageWithDicomSupport(imagePath, out var image, out error))
                {
                    return false;
                }

                using (image)
                {
                    var imageInfo = ConvertToManaged(image);

                    if (imageInfo.Dimension != 3)
                    {
                        error = $"Only 3D volumes are supported. Input dimension: {imageInfo.Dimension}.";
                        return false;
                    }

                    if (imageInfo.ComponentsPerPixel != 1)
                    {
                        error = $"Only scalar images are supported. ComponentsPerPixel: {imageInfo.ComponentsPerPixel}.";
                        return false;
                    }

                    if (!TryGet3DSize(imageInfo, out var width, out var height, out var depth, out var voxelCount, out error))
                    {
                        return false;
                    }

                    using var imageFloat = SimpleITK.Cast(image, PixelIDValueEnum.sitkFloat32);
                    var workingImage = imageFloat;
                    var workingImageOwned = false;
                    try
                    {
                        if (processingOptions.EnablePreGaussianSmoothing)
                        {
                            var sigma = Math.Max(0.0, processingOptions.PreGaussianSigma);
                            using var gaussianFilter = new SmoothingRecursiveGaussianImageFilter();
                            gaussianFilter.SetSigma(sigma);
                            var next = gaussianFilter.Execute(workingImage);
                            if (workingImageOwned)
                            {
                                workingImage.Dispose();
                            }
                            workingImage = next;
                            workingImageOwned = true;
                        }

                        if (processingOptions.EnablePreMedianFilter)
                        {
                            var radius = Math.Max(1u, processingOptions.PreMedianRadius);
                            using var medianFilter = new MedianImageFilter();
                            medianFilter.SetRadius(radius);
                            var next = medianFilter.Execute(workingImage);
                            if (workingImageOwned)
                            {
                                workingImage.Dispose();
                            }
                            workingImage = next;
                            workingImageOwned = true;
                        }

                        if (processingOptions.EnablePreBilateralFilter)
                        {
                            var domainSigma = Math.Max(0.1, processingOptions.PreBilateralDomainSigma);
                            var rangeSigma = Math.Max(1e-6, processingOptions.PreBilateralRangeSigma);
                            var rangeSamples = Math.Max(4u, processingOptions.PreBilateralRangeGaussianSamples);
                            using var bilateralFilter = new BilateralImageFilter();
                            bilateralFilter.SetDomainSigma(domainSigma);
                            bilateralFilter.SetRangeSigma(rangeSigma);
                            bilateralFilter.SetNumberOfRangeGaussianSamples(rangeSamples);
                            var next = bilateralFilter.Execute(workingImage);
                            if (workingImageOwned)
                            {
                                workingImage.Dispose();
                            }
                            workingImage = next;
                            workingImageOwned = true;
                        }

                        if (processingOptions.EnablePostAdaptiveHistogramEqualization)
                        {
                            var radius = Math.Max(1u, processingOptions.PostAdaptiveHistogramRadius);
                            using var adaptiveHistogramFilter = new AdaptiveHistogramEqualizationImageFilter();
                            adaptiveHistogramFilter.SetRadius(radius);
                            adaptiveHistogramFilter.SetAlpha((float)Math.Clamp(processingOptions.PostAdaptiveHistogramAlpha, 0.0, 1.0));
                            adaptiveHistogramFilter.SetBeta((float)Math.Clamp(processingOptions.PostAdaptiveHistogramBeta, 0.0, 1.0));
                            var next = adaptiveHistogramFilter.Execute(workingImage);
                            if (workingImageOwned)
                            {
                                workingImage.Dispose();
                            }
                            workingImage = next;
                            workingImageOwned = true;
                        }

                        using var statisticsFilter = new StatisticsImageFilter();
                        statisticsFilter.Execute(workingImage);

                        var minimum = statisticsFilter.GetMinimum();
                        var maximum = statisticsFilter.GetMaximum();
                        var range = maximum - minimum;

                        var sourcePointer = workingImage.GetConstBufferAsFloat();
                        if (sourcePointer == IntPtr.Zero)
                        {
                            error = "SimpleITK returned a null source buffer.";
                            return false;
                        }

                        var sourceBuffer = new float[voxelCount];
                        Marshal.Copy(sourcePointer, sourceBuffer, 0, voxelCount);

                        if (range <= double.Epsilon)
                        {
                            Array.Clear(sourceBuffer, 0, sourceBuffer.Length);
                        }
                        else
                        {
                            for (var i = 0; i < sourceBuffer.Length; ++i)
                            {
                                var normalized = ((double)sourceBuffer[i] - minimum) / range;
                                sourceBuffer[i] = (float)Math.Clamp(normalized, 0.0, 1.0);
                            }
                        }

                        volumeData = new SimpleITKVolumeData
                        {
                            Width = width,
                            Height = height,
                            Depth = depth,
                            VoxelsNormalized = sourceBuffer,
                            ImageInfo = imageInfo,
                            SourceMinimum = (float)minimum,
                            SourceMaximum = (float)maximum
                        };

                        return true;
                    }
                    finally
                    {
                        if (workingImageOwned)
                        {
                            workingImage.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = BuildPluginLoadError(ex);
                return false;
            }
        }

        public static bool TryReadVolumeFloat32Raw(
            string imagePath,
            out SimpleITKScalarVolumeData volumeData,
            out string error)
        {
            volumeData = default;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "imagePath must not be empty.";
                return false;
            }

            try
            {
                if (!TryReadImageWithDicomSupport(imagePath, out var image, out error))
                {
                    return false;
                }

                using (image)
                {
                    var imageInfo = ConvertToManaged(image);

                    if (imageInfo.Dimension != 3)
                    {
                        error = $"Only 3D volumes are supported. Input dimension: {imageInfo.Dimension}.";
                        return false;
                    }

                    if (imageInfo.ComponentsPerPixel != 1)
                    {
                        error = $"Only scalar images are supported. ComponentsPerPixel: {imageInfo.ComponentsPerPixel}.";
                        return false;
                    }

                    if (!TryGet3DSize(imageInfo, out var width, out var height, out var depth, out var voxelCount, out error))
                    {
                        return false;
                    }

                    using var imageFloat = SimpleITK.Cast(image, PixelIDValueEnum.sitkFloat32);
                    using var statisticsFilter = new StatisticsImageFilter();
                    statisticsFilter.Execute(imageFloat);

                    var sourcePointer = imageFloat.GetConstBufferAsFloat();
                    if (sourcePointer == IntPtr.Zero)
                    {
                        error = "SimpleITK returned a null source buffer.";
                        return false;
                    }

                    var sourceBuffer = new float[voxelCount];
                    Marshal.Copy(sourcePointer, sourceBuffer, 0, voxelCount);

                    volumeData = new SimpleITKScalarVolumeData
                    {
                        Width = width,
                        Height = height,
                        Depth = depth,
                        Voxels = sourceBuffer,
                        ImageInfo = imageInfo,
                        SourceMinimum = (float)statisticsFilter.GetMinimum(),
                        SourceMaximum = (float)statisticsFilter.GetMaximum()
                    };

                    return true;
                }
            }
            catch (Exception ex)
            {
                error = BuildPluginLoadError(ex);
                return false;
            }
        }

        public static bool TryReadLabelVolumeUInt8(
            string imagePath,
            out SimpleITKLabelVolumeData volumeData,
            out string error)
        {
            volumeData = default;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "imagePath must not be empty.";
                return false;
            }

            try
            {
                if (!TryReadImageWithDicomSupport(imagePath, out var image, out error))
                {
                    return false;
                }

                using (image)
                {
                    var imageInfo = ConvertToManaged(image);

                    if (imageInfo.Dimension != 3)
                    {
                        error = $"Only 3D volumes are supported. Input dimension: {imageInfo.Dimension}.";
                        return false;
                    }

                    if (imageInfo.ComponentsPerPixel != 1)
                    {
                        error = $"Only scalar label images are supported. ComponentsPerPixel: {imageInfo.ComponentsPerPixel}.";
                        return false;
                    }

                    if (!TryGet3DSize(imageInfo, out var width, out var height, out var depth, out var voxelCount, out error))
                    {
                        return false;
                    }

                    using var imageFloat = SimpleITK.Cast(image, PixelIDValueEnum.sitkFloat32);
                    var sourcePointer = imageFloat.GetConstBufferAsFloat();
                    if (sourcePointer == IntPtr.Zero)
                    {
                        error = "SimpleITK returned a null source buffer.";
                        return false;
                    }

                    var sourceBuffer = new float[voxelCount];
                    Marshal.Copy(sourcePointer, sourceBuffer, 0, voxelCount);

                    var labels = new byte[voxelCount];
                    byte labelMaximum = 0;
                    for (var i = 0; i < voxelCount; ++i)
                    {
                        var label = (byte)Math.Clamp((int)Math.Round(sourceBuffer[i]), 0, 255);
                        labels[i] = label;
                        if (label > labelMaximum)
                        {
                            labelMaximum = label;
                        }
                    }

                    volumeData = new SimpleITKLabelVolumeData
                    {
                        Width = width,
                        Height = height,
                        Depth = depth,
                        Voxels = labels,
                        ImageInfo = imageInfo,
                        LabelMaximum = labelMaximum
                    };

                    return true;
                }
            }
            catch (Exception ex)
            {
                error = BuildPluginLoadError(ex);
                return false;
            }
        }

        public static bool TryWriteLabelVolumeUInt8(
            string imagePath,
            SimpleITKLabelVolumeData volumeData,
            bool useCompression,
            out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "imagePath must not be empty.";
                return false;
            }

            if (volumeData.Width <= 0 || volumeData.Height <= 0 || volumeData.Depth <= 0)
            {
                error =
                    $"Label volume dimensions must be positive, received ({volumeData.Width}, {volumeData.Height}, {volumeData.Depth}).";
                return false;
            }

            var expectedVoxelCount = volumeData.Width * volumeData.Height * volumeData.Depth;
            if (volumeData.Voxels == null || volumeData.Voxels.Length != expectedVoxelCount)
            {
                error =
                    $"Label volume voxel buffer length mismatch. Expected {expectedVoxelCount}, " +
                    $"received {volumeData.Voxels?.Length ?? 0}.";
                return false;
            }

            try
            {
                var parentDirectory = Path.GetDirectoryName(imagePath);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                using var importer = new ImportImageFilter();
                using var size = new VectorUInt32(new[]
                {
                    (uint)volumeData.Width,
                    (uint)volumeData.Height,
                    (uint)volumeData.Depth
                });
                importer.SetSize(size);

                if (TryCreateVectorDouble(volumeData.ImageInfo.Spacing, 3, out var spacing))
                {
                    using (spacing)
                    {
                        importer.SetSpacing(spacing);
                    }
                }

                if (TryCreateVectorDouble(volumeData.ImageInfo.Origin, 3, out var origin))
                {
                    using (origin)
                    {
                        importer.SetOrigin(origin);
                    }
                }

                if (TryCreateVectorDouble(volumeData.ImageInfo.Direction, 9, out var direction))
                {
                    using (direction)
                    {
                        importer.SetDirection(direction);
                    }
                }

                var bufferHandle = GCHandle.Alloc(volumeData.Voxels, GCHandleType.Pinned);
                try
                {
                    importer.SetBufferAsUInt8(bufferHandle.AddrOfPinnedObject());
                    using var image = importer.Execute();
                    SimpleITK.WriteImage(image, imagePath, useCompression);
                }
                finally
                {
                    if (bufferHandle.IsAllocated)
                    {
                        bufferHandle.Free();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = BuildPluginLoadError(ex);
                return false;
            }
        }

        private static SimpleITKImageInfo ConvertToManaged(Image image)
        {
            var dimension = (int)image.GetDimension();
            var boundedDimension = Math.Max(0, Math.Min(dimension, 4));

            var size = new ulong[boundedDimension];
            var spacing = new double[boundedDimension];
            var origin = new double[boundedDimension];
            var direction = new double[boundedDimension * boundedDimension];

            using var nativeSize = image.GetSize();
            using var nativeSpacing = image.GetSpacing();
            using var nativeOrigin = image.GetOrigin();
            using var nativeDirection = image.GetDirection();

            for (var i = 0; i < boundedDimension; ++i)
            {
                if (i < nativeSize.Count)
                {
                    size[i] = nativeSize[i];
                }
                if (i < nativeSpacing.Count)
                {
                    spacing[i] = nativeSpacing[i];
                }
                if (i < nativeOrigin.Count)
                {
                    origin[i] = nativeOrigin[i];
                }
            }

            for (var i = 0; i < direction.Length; ++i)
            {
                if (i < nativeDirection.Count)
                {
                    direction[i] = nativeDirection[i];
                }
            }

            return new SimpleITKImageInfo
            {
                Dimension = dimension,
                Size = size,
                Spacing = spacing,
                Origin = origin,
                Direction = direction,
                PixelId = image.GetPixelIDValue(),
                ComponentsPerPixel = (int)image.GetNumberOfComponentsPerPixel()
            };
        }

        private static bool TryCreateVectorDouble(double[] values, int minimumCount, out VectorDouble vector)
        {
            vector = null;

            if (values == null || values.Length < minimumCount)
            {
                return false;
            }

            var buffer = new double[minimumCount];
            Array.Copy(values, buffer, minimumCount);
            vector = new VectorDouble(buffer);
            return true;
        }

        private static string BuildPluginLoadError(Exception exception)
        {
            var root = exception;
            while (root.InnerException != null)
            {
                root = root.InnerException;
            }

            return $"Failed to load/use SimpleITK native runtime: {root.Message}";
        }

        private static bool TryReadImageWithDicomSupport(string imagePath, out Image image, out string error)
        {
            image = null;
            error = string.Empty;

            if (Directory.Exists(imagePath))
            {
                return TryReadDicomSeriesFromDirectory(imagePath, preferredFilePath: null, out image, out error);
            }

            try
            {
                image = SimpleITK.ReadImage(imagePath);

                // If a single DICOM slice was provided, prefer its full series from the same directory.
                if (LooksLikeDicomFilePath(imagePath))
                {
                    var shouldTrySeriesRead = image.GetDimension() < 3;
                    if (!shouldTrySeriesRead && image.GetDimension() == 3)
                    {
                        using var size = image.GetSize();
                        shouldTrySeriesRead = size.Count >= 3 && size[2] <= 1;
                    }

                    if (shouldTrySeriesRead)
                    {
                        image.Dispose();
                        image = null;

                        var directory = Path.GetDirectoryName(imagePath);
                        if (!string.IsNullOrWhiteSpace(directory) &&
                            TryReadDicomSeriesFromDirectory(directory, imagePath, out image, out error))
                        {
                            return true;
                        }

                        // Fall back to single-slice read if series construction fails.
                        image = SimpleITK.ReadImage(imagePath);
                        error = string.Empty;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                if (LooksLikeDicomFilePath(imagePath))
                {
                    var directory = Path.GetDirectoryName(imagePath);
                    if (!string.IsNullOrWhiteSpace(directory) &&
                        TryReadDicomSeriesFromDirectory(directory, imagePath, out image, out error))
                    {
                        return true;
                    }
                }

                error = BuildPluginLoadError(ex);
                return false;
            }
        }

        private static bool TryReadDicomSeriesFromDirectory(
            string directoryPath,
            string preferredFilePath,
            out Image image,
            out string error)
        {
            image = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                error = $"DICOM directory does not exist: {directoryPath}";
                return false;
            }

            try
            {
                using var seriesIds = ImageSeriesReader.GetGDCMSeriesIDs(directoryPath);
                if (seriesIds == null || seriesIds.Count == 0)
                {
                    error = $"No DICOM series found in directory: {directoryPath}";
                    return false;
                }

                string[] selectedFileNames = null;
                var maxFileCount = -1;

                var preferredFullPath = string.IsNullOrWhiteSpace(preferredFilePath)
                    ? null
                    : Path.GetFullPath(preferredFilePath);

                for (var i = 0; i < seriesIds.Count; ++i)
                {
                    var seriesId = seriesIds[i];
                    using var fileNames = ImageSeriesReader.GetGDCMSeriesFileNames(directoryPath, seriesId);
                    if (fileNames == null || fileNames.Count == 0)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(preferredFullPath))
                    {
                        for (var j = 0; j < fileNames.Count; ++j)
                        {
                            if (string.Equals(Path.GetFullPath(fileNames[j]), preferredFullPath, StringComparison.OrdinalIgnoreCase))
                            {
                                selectedFileNames = fileNames.ToArray();
                                break;
                            }
                        }
                    }

                    if (selectedFileNames != null)
                    {
                        break;
                    }

                    if (fileNames.Count > maxFileCount)
                    {
                        maxFileCount = fileNames.Count;
                        selectedFileNames = fileNames.ToArray();
                    }
                }

                if (selectedFileNames == null || selectedFileNames.Length == 0)
                {
                    error = $"Found DICOM series IDs but no readable files in: {directoryPath}";
                    return false;
                }

                using var reader = new ImageSeriesReader();
                using var selectedFileNamesVector = new VectorString(selectedFileNames);
                reader.SetFileNames(selectedFileNamesVector);
                image = reader.Execute();
                return true;
            }
            catch (Exception ex)
            {
                error = BuildPluginLoadError(ex);
                return false;
            }
        }

        private static bool LooksLikeDicomFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".dcm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".dicom", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".ima", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Many DICOM exports use extensionless numeric filenames.
            return string.IsNullOrEmpty(extension);
        }

        private static bool TryGet3DSize(
            SimpleITKImageInfo info,
            out int width,
            out int height,
            out int depth,
            out int voxelCount,
            out string error)
        {
            width = 0;
            height = 0;
            depth = 0;
            voxelCount = 0;
            error = string.Empty;

            if (info.Size == null || info.Size.Length < 3)
            {
                error = "Image size metadata is invalid.";
                return false;
            }

            if (info.Size[0] == 0 || info.Size[1] == 0 || info.Size[2] == 0)
            {
                error = "Volume dimensions must be greater than zero.";
                return false;
            }

            if (info.Size[0] > int.MaxValue || info.Size[1] > int.MaxValue || info.Size[2] > int.MaxValue)
            {
                error = "Volume dimensions exceed supported integer range.";
                return false;
            }

            width = (int)info.Size[0];
            height = (int)info.Size[1];
            depth = (int)info.Size[2];

            var voxelCountLong = (long)width * height * depth;
            if (voxelCountLong <= 0 || voxelCountLong > int.MaxValue)
            {
                error = "Volume voxel count exceeds supported managed array range.";
                return false;
            }

            voxelCount = (int)voxelCountLong;
            return true;
        }
    }
}
