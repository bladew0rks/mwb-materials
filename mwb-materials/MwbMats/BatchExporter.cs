using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace mwb_materials.MwbMats
{
    class BatchExporter
    {
        public struct BatchProperties
        {
            public string VmtRootPath { get; set; }
            public string EnvRootPath { get; set; }
            public bool bMoveOutput { get; set; }
            public bool bIncludeFolders { get; set; }
            public MaterialManipulation.GenerateProperties GenerateProps { get; set; }
            public string AlbedoCompression { get; set; }
            public string NormalCompression { get; set; }
            public string ExponentCompression { get; set; }
            public bool bAlbedoMipMaps { get; set; }
            public bool bNormalMipMaps { get; set; }
            public bool bExponentMipMaps { get; set; }
            public bool bKeepIntermediates { get; set; }
            public bool bUseModelMaterialNames { get; set; }
            public float AlphatestReference { get; set; }
            public VmtPreset VmtPreset { get; set; }

            public int MaxParallelJobs { get; set; }

            public Action<string> LogFunc { get; set; }
        }

        public sealed class BatchProgress
        {
            public BatchProgress(int completed, int total, string current)
            {
                Completed = completed;
                Total = total;
                Current = current;
            }

            public int Completed { get; }
            public int Total { get; }
            public string Current { get; }
        }

        private sealed class PendingJob
        {
            public string FolderPath;
            public string DebugPath;
            public TextureGenerationJob Job;
        }

        public static async Task<int> StartBatch(string path, BatchProperties props, IProgress<BatchProgress> progress, CancellationToken cancellationToken = default)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            List<PendingJob> jobs = new List<PendingJob>();
            DiscoverInFolder(path, props, path, jobs);

            if (jobs.Count == 0)
            {
                props.LogFunc?.Invoke("No source textures found in " + path);
                return 0;
            }

            int maxParallel = Math.Max(1, props.MaxParallelJobs);
            int completed = 0;
            object progressGate = new object();
            HashSet<string> running = new HashSet<string>();

            props.LogFunc?.Invoke("Found " + jobs.Count + " material" + (jobs.Count == 1 ? "" : "s") + "; processing up to " + maxParallel + " at a time on " + Environment.ProcessorCount + " threads.");
            progress?.Report(new BatchProgress(0, jobs.Count, string.Empty));

            void ReportProgress()
            {
                progress?.Report(new BatchProgress(completed, jobs.Count, string.Join(", ", running)));
            }

            using CancellationTokenSource pipeline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using SemaphoreSlim decodeSlots = new SemaphoreSlim(maxParallel + 1);
            TaskCompletionSource<MaterialManipulation.SourceSet>[] decoded = jobs
                .Select(_ => new TaskCompletionSource<MaterialManipulation.SourceSet>(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();

            Task decoder = Task.Run(async () =>
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    try
                    {
                        await decodeSlots.WaitAsync(pipeline.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        for (int j = i; j < jobs.Count; j++)
                        {
                            decoded[j].TrySetCanceled(pipeline.Token);
                        }

                        return;
                    }

                    int index = i;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            decoded[index].TrySetResult(await MaterialManipulation.LoadSources(jobs[index].Job.Files, pipeline.Token));
                        }
                        catch (Exception ex)
                        {
                            decoded[index].TrySetException(ex);
                        }
                    });
                }
            });

            ParallelOptions options = new ParallelOptions()
            {
                MaxDegreeOfParallelism = maxParallel,
                CancellationToken = pipeline.Token
            };

            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, jobs.Count), options, async (index, token) =>
                {
                    PendingJob pending = jobs[index];
                    MaterialManipulation.SourceSet sources = await decoded[index].Task.WaitAsync(token);

                    lock (progressGate)
                    {
                        running.Add(pending.Job.DisplayName);
                        ReportProgress();
                    }

                    BatchProperties jobProps = props;

                    if (maxParallel > 1 && props.LogFunc != null)
                    {
                        string prefix = "[" + pending.Job.DisplayName + "] ";
                        Action<string> log = props.LogFunc;
                        jobProps.LogFunc = message => log(prefix + message);
                        MaterialManipulation.GenerateProperties generateProps = jobProps.GenerateProps;
                        generateProps.LogFunc = jobProps.LogFunc;
                        jobProps.GenerateProps = generateProps;
                    }

                    jobProps.LogFunc?.Invoke("Processing " + pending.Job.DisplayName + " (" + pending.Job.Files.Count + " source textures)");

                    try
                    {
                        await GenerateJob(pending.FolderPath, path, pending.DebugPath, pending.Job, sources, jobProps, token);
                    }
                    finally
                    {
                        decoded[index] = null;
                        decodeSlots.Release();
                    }

                    lock (progressGate)
                    {
                        running.Remove(pending.Job.DisplayName);
                        completed++;
                        ReportProgress();
                    }
                });
            }
            finally
            {
                pipeline.Cancel();
                await decoder;
            }

            return jobs.Count;
        }

        private static void DiscoverInFolder(string path, BatchProperties props, string startPath, List<PendingJob> jobs)
        {
            string[] folders = Directory.GetDirectories(path)
                .Where(folder => !IsBatchIgnoredFolder(folder))
                .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            List<TextureGenerationJob> generatedJobs = null;
            bool generateBeforeChildren = props.bUseModelMaterialNames && HasGltfFile(path);

            if (generateBeforeChildren)
            {
                generatedJobs = DiscoverCurrentFolder(path, props, startPath, jobs);
            }

            foreach (string folder in folders)
            {
                if (IsTextureFolderClaimedByGeneratedJobs(folder, generatedJobs))
                {
                    props.LogFunc?.Invoke("Skipping " + folder + " because its textures were claimed by a model material binding.");
                    continue;
                }

                DiscoverInFolder(folder, props, startPath, jobs);
            }

            if (!generateBeforeChildren)
            {
                DiscoverCurrentFolder(path, props, startPath, jobs);
            }
        }

        private static List<TextureGenerationJob> DiscoverCurrentFolder(string path, BatchProperties props, string startPath, List<PendingJob> pendingJobs)
        {
            List<string> sanitizedFiles = Directory.GetFiles(path)
                .Where(ImageLoader.IsSupportedImage)
                .ToList();

            if (sanitizedFiles.Count <= 0 && !props.bUseModelMaterialNames)
            {
                return new List<TextureGenerationJob>();
            }

            string folderName = Path.GetFileName(path);
            List<TextureGenerationJob> jobs = props.bUseModelMaterialNames
                ? ModelMaterialResolver.Resolve(path, startPath, sanitizedFiles, props.LogFunc)
                : new List<TextureGenerationJob>() { new TextureGenerationJob(folderName, string.Empty, folderName + ".vmt", folderName, sanitizedFiles) };

            jobs = jobs.Where(job => job.Files.Count > 0).ToList();

            if (jobs.Count <= 0)
            {
                return new List<TextureGenerationJob>();
            }

            string debugPath = null;

            if (props.bKeepIntermediates)
            {
                debugPath = Path.Combine(path, "temp_debug");
                TryDeleteDirectory(debugPath, props.LogFunc);
                Directory.CreateDirectory(debugPath);
                props.LogFunc?.Invoke("Keeping intermediate textures in " + debugPath);
            }

            foreach (TextureGenerationJob job in jobs)
            {
                pendingJobs.Add(new PendingJob() { FolderPath = path, DebugPath = debugPath, Job = job });
            }

            return jobs;
        }

        private static bool IsBatchIgnoredFolder(string folder)
        {
            string folderNameOnly = Path.GetFileName(folder);

            return string.Equals(folderNameOnly, "output", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(folderNameOnly, "temp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(folderNameOnly, "temp_debug", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGltfFile(string folder)
        {
            return Directory.GetFiles(folder).Any(file =>
                string.Equals(Path.GetExtension(file), ".gltf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(file), ".glb", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTextureFolderClaimedByGeneratedJobs(string folder, List<TextureGenerationJob> generatedJobs)
        {
            if (generatedJobs == null || generatedJobs.Count == 0)
            {
                return false;
            }

            string folderRoot = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return generatedJobs
                .SelectMany(job => job.Files)
                .Any(file => Path.GetFullPath(file).StartsWith(folderRoot, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task GenerateJob(string path, string startPath, string debugPath, TextureGenerationJob job, MaterialManipulation.SourceSet sources, BatchProperties props, CancellationToken cancellationToken)
        {
            bool surfaceGgx = props.VmtPreset != null && props.VmtPreset.IsSurfaceGgx;
            MaterialManipulation.GenerateProperties generateProps = props.GenerateProps;
            generateProps.bSurfaceGgx = surfaceGgx;
            MaterialManipulation.SourceTextureSet textures = await MaterialManipulation.GenerateTextures(sources, generateProps, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            //resolve opacity-related settings
            MaterialManipulation.OpacityMode opacityMode = textures.OpacityMode;
            string effectiveAlbedoCompression = props.AlbedoCompression;

            if (opacityMode != MaterialManipulation.OpacityMode.None && textures.Albedo == null)
            {
                props.LogFunc?.Invoke("Warning: opacity mask provided without albedo texture; opacity will be ignored.");
                opacityMode = MaterialManipulation.OpacityMode.None;
            }

            if (opacityMode != MaterialManipulation.OpacityMode.None)
            {
                if (effectiveAlbedoCompression == TextureExporter.FormatDXT1)
                {
                    effectiveAlbedoCompression = TextureExporter.FormatDXT5;
                    props.LogFunc?.Invoke("Albedo compression upgraded from DXT1 to DXT5 (opacity requires alpha channel)");
                }
            }

            if (debugPath != null)
            {
                SaveDebugTextures(textures, debugPath, job.TextureBaseName, props.LogFunc);
            }

            Dictionary<string, object> vmtValues = new Dictionary<string, object>();

            string movePath = string.Empty;
            string detailName = string.Empty;
            string outputRootPath = Path.Combine(path, "output");
            string jobOutputPath = string.IsNullOrEmpty(job.RelativeFolder) ? outputRootPath : Path.Combine(outputRootPath, job.RelativeFolder);
            string qcMaterialDirectory = QcMaterialResolver.ResolveMaterialDirectory(path, startPath, props.LogFunc);

            if (!string.IsNullOrEmpty(qcMaterialDirectory))
            {
                movePath = qcMaterialDirectory;

                if (!string.IsNullOrEmpty(job.RelativeFolder))
                {
                    movePath = Path.Combine(movePath, job.RelativeFolder);
                }
            }
            else if (props.bMoveOutput && !string.IsNullOrEmpty(props.VmtRootPath))
            {
                movePath = props.VmtRootPath;

                if (props.bIncludeFolders)
                {
                    movePath = CombineWithRelativeFolder(movePath, startPath, path);
                }

                if (!string.IsNullOrEmpty(job.RelativeFolder))
                {
                    movePath = Path.Combine(movePath, job.RelativeFolder);
                }
            }

            string exportPath = string.IsNullOrEmpty(movePath) ? jobOutputPath : movePath;
            List<Task> exports = new List<Task>();

            void QueueExport(PixelBuffer image, string outputName, string compression, bool mipmaps, bool alphaIsCoverage = false)
            {
                exports.Add(Task.Run(() => TextureExporter.Export(image, exportPath, outputName, compression, mipmaps, props.LogFunc, alphaIsCoverage), cancellationToken));
            }

            if (textures.Albedo != null)
            {
                string outputName = job.TextureBaseName + "_rgb";
                vmtValues.Add("ALBEDONAME", outputName);
                QueueExport(textures.Albedo, outputName, effectiveAlbedoCompression, props.bAlbedoMipMaps, opacityMode != MaterialManipulation.OpacityMode.None);
            }

            if (textures.Exponent != null)
            {
                string outputName = job.TextureBaseName + "_e";
                vmtValues.Add("EXPONENTNAME", outputName);
                QueueExport(textures.Exponent, outputName, props.ExponentCompression, props.bExponentMipMaps);
            }

            if (textures.Normal != null)
            {
                string outputName = job.TextureBaseName + "_n";
                vmtValues.Add("NORMALNAME", outputName);
                QueueExport(textures.Normal, outputName, props.NormalCompression, props.bNormalMipMaps);
            }

            if (textures.Emissive != null)
            {
                string outputName = job.TextureBaseName + "_emissive";
                detailName = outputName;
                QueueExport(textures.Emissive, outputName, props.AlbedoCompression, props.bAlbedoMipMaps);
            }

            await Task.WhenAll(exports);

            Color averageMetallicColor = textures.AverageMetallicColor;
            double averageRoughness = textures.AverageRoughness;

            string vmtExportPath = VmtUtils.GetVMTPath(movePath, "Output", props.LogFunc);

            foreach (string key in new[] { "ALBEDO", "NORMAL", "EXPONENT" })
            {
                if (vmtValues.TryGetValue(key + "NAME", out object textureName))
                {
                    vmtValues.Add(key + "PATH", VmtUtils.JoinVMTPath(vmtExportPath, textureName.ToString()));
                }
            }

            vmtValues.Add("DETAILBLOCK", GetDetailBlock(vmtExportPath, detailName));

            //opacity
            if (opacityMode == MaterialManipulation.OpacityMode.Alphatest)
            {
                vmtValues.Add("BLENDTINTBYBASEALPHA", "0");
                vmtValues.Add("OPACITYBLOCK", GetOpacityBlock(true, props.AlphatestReference));
            }
            else if (opacityMode == MaterialManipulation.OpacityMode.Translucent)
            {
                vmtValues.Add("BLENDTINTBYBASEALPHA", "0");
                vmtValues.Add("OPACITYBLOCK", GetOpacityBlock(false, 0.0f));
            }
            else
            {
                vmtValues.Add("BLENDTINTBYBASEALPHA", "1");
                vmtValues.Add("OPACITYBLOCK", string.Empty);
            }

            if (surfaceGgx)
            {
                if (textures.HasMetalness)
                {
                    vmtValues.Add("METAL", "1");
                }

                VmtGenerator.Generate(exportPath, job.VmtFileName, vmtValues, props.VmtPreset, props.LogFunc);
                return;
            }

            //envmap
            VmtUtils.EnvMapFile envmapTexture = VmtUtils.GetEnvMapTextureFromRoughness(averageRoughness);
            vmtValues.Add("ENVMAP", envmapTexture.Name);
            vmtValues.Add("ENVMAPTINT", VmtUtils.GetVMTVector(averageMetallicColor));

            string envPath;
            string vmtEnvPath;

            if (!string.IsNullOrEmpty(props.EnvRootPath))
            {
                envPath = props.EnvRootPath;
                vmtEnvPath = VmtUtils.GetVMTPath(envPath, "Envmaps", props.LogFunc);
            }
            else
            {
                envPath = exportPath;
                vmtEnvPath = vmtExportPath;
            }

            vmtValues.Add("ENVMAPFILE", VmtUtils.JoinVMTPath(vmtEnvPath, envmapTexture.Name));
            Directory.CreateDirectory(envPath);
            TextureExporter.WriteAllBytesLocked(Path.Combine(envPath, envmapTexture.Name + ".vtf"), envmapTexture.Content);

            //generate vmt
            VmtGenerator.Generate(exportPath, job.VmtFileName, vmtValues, props.VmtPreset, props.LogFunc);
        }

        private static string GetDetailBlock(string exportPath, string detailName)
        {
            if (string.IsNullOrEmpty(detailName))
            {
                return string.Empty;
            }

            string detailPath = VmtUtils.JoinVMTPath(exportPath, detailName);

            return
                "    \"$detail\" \"" + detailPath + "\"\r\n" +
                "    \"$detailscale\" \"1\"\r\n" +
                "    \"$detailblendmode\" \"5\"";
        }

        private static string GetOpacityBlock(bool bAlphatest, float alphatestReference)
        {
            if (bAlphatest)
            {
                string refValue = Math.Round(alphatestReference, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);

                return
                    "    \"$alphatest\" \"1\"\r\n" +
                    "    \"$alphatestreference\" \"" + refValue + "\"\r\n" +
                    "    \"$allowalphatocoverage\" \"1\"";
            }

            return "    \"$translucent\" \"1\"";
        }

        private static void TryDeleteDirectory(string directory, Action<string> logFunc)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (Exception ex)
            {
                logFunc?.Invoke("Warning: could not delete folder " + directory + ": " + ex.Message);
            }
        }

        private static void SaveDebugTextures(MaterialManipulation.SourceTextureSet textures, string debugPath, string folderName, Action<string> logFunc)
        {
            SaveDebugTexture(textures.Albedo, debugPath, folderName + "_debug_final_albedo_before_vtf.png", logFunc);
            SaveDebugTexture(textures.Normal, debugPath, folderName + "_debug_final_normal_before_vtf.png", logFunc);
            SaveDebugTexture(textures.Exponent, debugPath, folderName + "_debug_exponent_phong_mask.png", logFunc);

            if (textures.Intermediates != null)
            {
                SaveDebugTexture(textures.Intermediates.Metalness, debugPath, folderName + "_debug_extracted_metalness.png", logFunc);
                SaveDebugTexture(textures.Intermediates.AmbientOcclusion, debugPath, folderName + "_debug_extracted_ao.png", logFunc);
                SaveDebugTexture(textures.Intermediates.Gloss, debugPath, folderName + "_debug_extracted_gloss.png", logFunc);
            }
        }

        private static void SaveDebugTexture(PixelBuffer bitmap, string debugPath, string fileName, Action<string> logFunc)
        {
            if (bitmap == null)
            {
                return;
            }

            string path = Path.Combine(debugPath, fileName);
            PngWriter.Save(bitmap, path);
            logFunc?.Invoke("Saved debug texture " + Path.GetFileName(path));
        }

        private static void SaveDebugTexture(GrayBuffer bitmap, string debugPath, string fileName, Action<string> logFunc)
        {
            if (bitmap == null)
            {
                return;
            }

            string path = Path.Combine(debugPath, fileName);
            PngWriter.Save(bitmap, path);
            logFunc?.Invoke("Saved debug texture " + Path.GetFileName(path));
        }

        private static string CombineWithRelativeFolder(string rootPath, string startPath, string currentPath)
        {
            string relativePath = Path.GetRelativePath(startPath, currentPath);

            if (string.IsNullOrEmpty(relativePath) || relativePath == ".")
            {
                return rootPath;
            }

            return Path.Combine(rootPath, relativePath);
        }
    }
}
