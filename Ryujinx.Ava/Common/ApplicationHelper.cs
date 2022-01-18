using Avalonia.Controls;
using Avalonia.Threading;
using LibHac;
using LibHac.Account;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Ncm;
using LibHac.Ns;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Ui.Controls;
using Ryujinx.Ava.Ui.Windows;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using System;
using System.Buffers;
using System.IO;
using System.Threading;
using static LibHac.Fs.ApplicationSaveDataManagement;
using ApplicationId = LibHac.Ncm.ApplicationId;
using Path = System.IO.Path;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;

namespace Ryujinx.Ava.Common
{
    public static class ApplicationHelper
    {
        private static HorizonClient _horizonClient;
        private static VirtualFileSystem _virtualFileSystem;
        private static StyleableWindow _owner;
        private static bool _cancel;

        public static void Initialize(VirtualFileSystem virtualFileSystem,HorizonClient horizonClient, StyleableWindow owner)
        {
            _owner = owner;
            _virtualFileSystem = virtualFileSystem;
            _horizonClient = horizonClient;
        }

        private static bool TryFindSaveData(string titleName, ulong titleId,
            BlitStruct<ApplicationControlProperty> controlHolder, SaveDataFilter filter, out ulong saveDataId)
        {
            saveDataId = default;

            Result result = _horizonClient.Fs.FindSaveDataWithFilter(out SaveDataInfo saveDataInfo,
                SaveDataSpaceId.User, in filter);

            if (ResultFs.TargetNotFound.Includes(result))
            {
                // Savedata was not found. Ask the user if they want to create it

                bool dialogResponse = false;

                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    dialogResponse = await ContentDialogHelper.CreateChoiceDialog(_owner, "Ryujinx", string.Format(LocaleManager.Instance["DialogMessageSaveNotAvailableMessage"], titleName, titleId),
                        LocaleManager.Instance["DialogMessageSaveNotAvailableCreateSaveMessage"]);
                }).Wait();

                if (!dialogResponse)
                {
                    return false;
                }

                ref ApplicationControlProperty control = ref controlHolder.Value;

                if (Utilities.IsZeros(controlHolder.ByteSpan))
                {
                    // If the current application doesn't have a loaded control property, create a dummy one
                    // and set the savedata sizes so a user savedata will be created.
                    control = ref new BlitStruct<ApplicationControlProperty>(1).Value;

                    // The set sizes don't actually matter as long as they're non-zero because we use directory savedata.
                    control.UserAccountSaveDataSize = 0x4000;
                    control.UserAccountSaveDataJournalSize = 0x4000;

                    Logger.Warning?.Print(LogClass.Application,
                        "No control file was found for this game. Using a dummy one instead. This may cause inaccuracies in some games.");
                }

                Uid user = new(1, 0); // TODO: Remove Hardcoded value.

                result = EnsureApplicationSaveData(_horizonClient.Fs, out _, new ApplicationId(titleId),
                    ref control, ref user);

                if (result.IsFailure())
                {
                    ContentDialogHelper.CreateErrorDialog(_owner, string.Format(LocaleManager.Instance["DialogMessageCreateSaveErrorMessage"], result.ToStringWithName()));

                    return false;
                }

                // Try to find the savedata again after creating it
                result = _horizonClient.Fs.FindSaveDataWithFilter(out saveDataInfo, SaveDataSpaceId.User,
                    in filter);
            }

            if (result.IsSuccess())
            {
                saveDataId = saveDataInfo.SaveDataId;

                return true;
            }

            ContentDialogHelper.CreateErrorDialog(_owner,
                string.Format(LocaleManager.Instance["DialogMessageFindSaveErrorMessage"], result.ToStringWithName()));

            return false;
        }

        public static void OpenSaveDir(SaveDataFilter saveDataFilter, ulong titleId,
            BlitStruct<ApplicationControlProperty> controlData, string titleName)
        {
            saveDataFilter.SetProgramId(new ProgramId(titleId));

            if (!TryFindSaveData(titleName, titleId, controlData, saveDataFilter, out ulong saveDataId))
            {
                return;
            }

            string saveRootPath = Path.Combine(_virtualFileSystem.GetNandPath(), $"user/save/{saveDataId:x16}");

            if (!Directory.Exists(saveRootPath))
            {
                // Inconsistent state. Create the directory
                Directory.CreateDirectory(saveRootPath);
            }

            string committedPath = Path.Combine(saveRootPath, "0");
            string workingPath = Path.Combine(saveRootPath, "1");

            // If the committed directory exists, that path will be loaded the next time the savedata is mounted
            if (Directory.Exists(committedPath))
            {
                OpenHelper.OpenFolder(committedPath);
            }
            else
            {
                // If the working directory exists and the committed directory doesn't,
                // the working directory will be loaded the next time the savedata is mounted
                if (!Directory.Exists(workingPath))
                {
                    Directory.CreateDirectory(workingPath);
                }

                OpenHelper.OpenFolder(workingPath);
            }
        }

        public static async void ExtractSection(NcaSectionType ncaSectionType, string titleFilePath,
            int programIndex = 0)
        {
            OpenFolderDialog folderDialog = new() {Title = LocaleManager.Instance["FolderDialogExtractTitle"]};

            string destination = await folderDialog.ShowAsync(_owner);

            _cancel = false;

            if (!string.IsNullOrWhiteSpace(destination))
            {
                Thread extractorThread = new(() =>
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        UserResult result = await ContentDialogHelper.CreateConfirmationDialog(_owner,
                                                                                               string.Format(LocaleManager.Instance["DialogNcaExtractionMessage"], ncaSectionType, Path.GetFileName(titleFilePath)),
                                                                                               "",
                                                                                               "",
                                                                                               LocaleManager.Instance["InputDialogCancel"],
                                                                                               LocaleManager.Instance["DialogNcaExtractionTitle"]);

                        if (result == UserResult.Cancel)
                        {
                            _cancel = true;
                        }
                    });

                    Thread.Sleep(1000);

                    using (FileStream file = new(titleFilePath, FileMode.Open, FileAccess.Read))
                    {
                        Nca mainNca = null;
                        Nca patchNca = null;

                        if (Path.GetExtension(titleFilePath).ToLower() == ".nsp" ||
                            Path.GetExtension(titleFilePath).ToLower() == ".pfs0" ||
                            Path.GetExtension(titleFilePath).ToLower() == ".xci")
                        {
                            PartitionFileSystem pfs;

                            if (Path.GetExtension(titleFilePath) == ".xci")
                            {
                                Xci xci = new(_virtualFileSystem.KeySet, file.AsStorage());

                                pfs = xci.OpenPartition(XciPartitionType.Secure);
                            }
                            else
                            {
                                pfs = new PartitionFileSystem(file.AsStorage());
                            }

                            foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                            {
                                using var ncaFile = new UniqueRef<IFile>();

                                pfs.OpenFile(ref ncaFile.Ref(), fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                Nca nca = new(_virtualFileSystem.KeySet, ncaFile.Get.AsStorage());

                                if (nca.Header.ContentType == NcaContentType.Program)
                                {
                                    int dataIndex =
                                        Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                                    if (nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                                    {
                                        patchNca = nca;
                                    }
                                    else
                                    {
                                        mainNca = nca;
                                    }
                                }
                            }
                        }
                        else if (Path.GetExtension(titleFilePath).ToLower() == ".nca")
                        {
                            mainNca = new Nca(_virtualFileSystem.KeySet, file.AsStorage());
                        }

                        if (mainNca == null)
                        {
                            Logger.Error?.Print(LogClass.Application,
                                "Extraction failure. The main NCA was not present in the selected file.");

                            Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                ContentDialogHelper.CreateErrorDialog(_owner,
                                    LocaleManager.Instance["DialogNcaExtractionMainNcaNotFoundErrorMessage"]);
                            });

                            return;
                        }

                        (Nca updatePatchNca, _) = ApplicationLoader.GetGameUpdateData(_virtualFileSystem,
                            mainNca.Header.TitleId.ToString("x16"), programIndex, out _);

                        if (updatePatchNca != null)
                        {
                            patchNca = updatePatchNca;
                        }

                        int index = Nca.GetSectionIndexFromType(ncaSectionType, mainNca.Header.ContentType);

                        try
                        {
                            IFileSystem ncaFileSystem = patchNca != null
                                ? mainNca.OpenFileSystemWithPatch(patchNca, index, IntegrityCheckLevel.ErrorOnInvalid)
                                : mainNca.OpenFileSystem(index, IntegrityCheckLevel.ErrorOnInvalid);

                            FileSystemClient fsClient = _horizonClient.Fs;

                            string source = DateTime.Now.ToFileTime().ToString()[10..];
                            string output = DateTime.Now.ToFileTime().ToString()[10..];

                            using var uniqueSourceFs = new UniqueRef<IFileSystem>(ncaFileSystem);
                            using var uniqueOutputFs = new UniqueRef<IFileSystem>(new LocalFileSystem(destination));

                            fsClient.Register(source.ToU8Span(), ref uniqueSourceFs.Ref());
                            fsClient.Register(output.ToU8Span(), ref uniqueOutputFs.Ref());

                            (Result? resultCode, bool canceled) = CopyDirectory(fsClient, $"{source}:/", $"{output}:/");

                            if (!canceled)
                            {
                                if (resultCode.Value.IsFailure())
                                {
                                    Logger.Error?.Print(LogClass.Application,
                                        $"LibHac returned error code: {resultCode.Value.ErrorCode}");

                                    Dispatcher.UIThread.InvokeAsync(() =>
                                    {
                                        ContentDialogHelper.CreateErrorDialog(_owner,
                                            LocaleManager.Instance["DialogNcaExtractionCheckLogErrorMessage"]);
                                    });
                                }
                                else if (resultCode.Value.IsSuccess())
                                {
                                    Dispatcher.UIThread.InvokeAsync(async () =>
                                    {
                                        await ContentDialogHelper.CreateInfoDialog(_owner, LocaleManager.Instance["DialogNcaExtractionSuccessMessage"], "", closeButton: "", title: LocaleManager.Instance["DialogNcaExtractionTitle"]);
                                    });
                                }
                            }

                            fsClient.Unmount(source.ToU8Span());
                            fsClient.Unmount(output.ToU8Span());
                        }
                        catch(ArgumentException ex)
                        {
                            Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                ContentDialogHelper.CreateErrorDialog(_owner, ex.Message);
                            });
                        }
                    }
                });

                extractorThread.Name = "GUI.NcaSectionExtractorThread";
                extractorThread.IsBackground = true;
                extractorThread.Start();
            }
        }

        public static (Result? result, bool canceled) CopyDirectory(FileSystemClient fs, string sourcePath,
            string destPath)
        {
            Result rc = fs.OpenDirectory(out DirectoryHandle sourceHandle, sourcePath.ToU8Span(),
                OpenDirectoryMode.All);
            if (rc.IsFailure())
            {
                return (rc, false);
            }

            using (sourceHandle)
            {
                foreach (DirectoryEntryEx entry in fs.EnumerateEntries(sourcePath, "*", SearchOptions.Default))
                {
                    if (_cancel)
                    {
                        return (null, true);
                    }

                    string subSrcPath = PathTools.Normalize(PathTools.Combine(sourcePath, entry.Name));
                    string subDstPath = PathTools.Normalize(PathTools.Combine(destPath, entry.Name));

                    if (entry.Type == DirectoryEntryType.Directory)
                    {
                        fs.EnsureDirectoryExists(subDstPath);

                        (Result? result, bool canceled) = CopyDirectory(fs, subSrcPath, subDstPath);
                        if (canceled || result.Value.IsFailure())
                        {
                            return (result, canceled);
                        }
                    }

                    if (entry.Type == DirectoryEntryType.File)
                    {
                        fs.CreateOrOverwriteFile(subDstPath, entry.Size);

                        rc = CopyFile(fs, subSrcPath, subDstPath);
                        if (rc.IsFailure())
                        {
                            return (rc, false);
                        }
                    }
                }
            }

            return (Result.Success, false);
        }

        public static Result CopyFile(FileSystemClient fs, string sourcePath, string destPath)
        {
            Result rc = fs.OpenFile(out FileHandle sourceHandle, sourcePath.ToU8Span(), OpenMode.Read);
            if (rc.IsFailure())
            {
                return rc;
            }

            using (sourceHandle)
            {
                rc = fs.OpenFile(out FileHandle destHandle, destPath.ToU8Span(), OpenMode.Write | OpenMode.AllowAppend);
                if (rc.IsFailure())
                {
                    return rc;
                }

                using (destHandle)
                {
                    const int maxBufferSize = 1024 * 1024;

                    rc = fs.GetFileSize(out long fileSize, sourceHandle);
                    if (rc.IsFailure())
                    {
                        return rc;
                    }

                    int bufferSize = (int)Math.Min(maxBufferSize, fileSize);

                    byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                    try
                    {
                        for (long offset = 0; offset < fileSize; offset += bufferSize)
                        {
                            int toRead = (int)Math.Min(fileSize - offset, bufferSize);
                            Span<byte> buf = buffer.AsSpan(0, toRead);

                            rc = fs.ReadFile(out long _, sourceHandle, offset, buf);
                            if (rc.IsFailure())
                            {
                                return rc;
                            }

                            rc = fs.WriteFile(destHandle, offset, buf, WriteOption.None);
                            if (rc.IsFailure())
                            {
                                return rc;
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    rc = fs.FlushFile(destHandle);
                    if (rc.IsFailure())
                    {
                        return rc;
                    }
                }
            }

            return Result.Success;
        }
    }
}