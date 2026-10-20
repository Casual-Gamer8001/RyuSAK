import React, { useEffect, useState } from "react";
import { Alert, Box } from "@mui/material";
import useTranslation from "../../i18n/I18nService";
import Swal from "sweetalert2";
import { ipcRenderer } from "electron";
import useStore from "../../actions/state";
import semver from "semver";

type UpdateProgress = {
  stage: "checking" | "downloading" | "verified" | "handoff" | "installing";
  version?: string;
  received?: number;
  total?: number;
};

type UpdateResult = { ok: boolean; error?: string; logPath?: string; openedReleasePage?: boolean };

type UpdateStatus = {
  result: "success" | "rolled-back" | "aborted" | "failed";
  targetVersion?: string;
  message?: string;
  logPath?: string;
  recoveryPath?: string;
};

const PROGRESS_ELEMENT_ID = "ryusak-update-progress";

const escapeHtml = (value: string) => value
  .replace(/&/g, "&amp;")
  .replace(/</g, "&lt;")
  .replace(/>/g, "&gt;")
  .replace(/"/g, "&quot;");

const toMegabytes = (bytes = 0) => (bytes / 1024 / 1024).toFixed(1);

const UpdateComponent = ({ state }: { state: "downloading" | "downloaded" }) => {
  const [latestVersion, currentVersion] = useStore(state => [state.latestVersion, state.currentVersion]);
  const [manualUpdateOnly, setManualUpdateOnly] = useState(false);
  const [manualUpdatePromptShown, setManualUpdatePromptShown] = useState(false);
  const [forceManualUpdatePrompt, setForceManualUpdatePrompt] = useState(false);
  const [lastUpdateReported, setLastUpdateReported] = useState(false);
  const { t } = useTranslation();
  const hasManualUpdate = manualUpdateOnly
    && currentVersion
    && latestVersion
    && (forceManualUpdatePrompt || semver.lt(currentVersion, latestVersion));

  const describeProgress = (progress: UpdateProgress) => {
    switch (progress.stage) {
      case "downloading": {
        const percent = progress.total ? Math.floor((progress.received || 0) / progress.total * 100) : 0;
        return t("updateDownloadingProgress")
          .replace("{version}", progress.version || "")
          .replace("{percent}", String(percent))
          .replace("{received}", toMegabytes(progress.received))
          .replace("{total}", toMegabytes(progress.total));
      }
      case "verified":
        return t("updateVerified");
      case "handoff":
        return t("updateHandoff");
      case "installing":
        return t("updateInstalling");
      default:
        return t("updatePreparing");
    }
  };

  const showFailure = async (text: string, details?: string, logPath?: string, icon: "error" | "info" = "error") => {
    const { isDenied } = await Swal.fire({
      icon,
      title: t("updateFailed"),
      html: `${escapeHtml(text)}${details ? `<br><br><small>${escapeHtml(details)}</small>` : ""}`,
      showDenyButton: !!logPath,
      denyButtonText: t("updateOpenLog")
    });

    if (isDenied && logPath) {
      ipcRenderer.invoke("open-update-log", logPath);
    }
  };

  // The update helper (or the Linux installer) records how the last update went before RyuSAK reopens.
  const reportLastUpdate = async (status: UpdateStatus) => {
    const fill = (key: "updateSucceeded" | "updateRolledBack" | "updateAborted" | "updateRecoveryNeeded") => t(key)
      .replace("{version}", status.targetVersion || "")
      .replace("{path}", status.recoveryPath || "");

    if (status.result === "success") {
      Swal.fire({
        toast: true,
        position: "bottom-end",
        icon: "success",
        title: fill("updateSucceeded"),
        showConfirmButton: false,
        timer: 6000,
        timerProgressBar: true
      });
      return;
    }

    if (status.result === "rolled-back") {
      await showFailure(fill("updateRolledBack"), status.message, status.logPath);
    } else if (status.result === "aborted") {
      await showFailure(fill("updateAborted"), status.message, status.logPath, "info");
    } else {
      await showFailure(fill("updateRecoveryNeeded"), status.message, status.logPath);
    }
  };

  useEffect(() => {
    const handleManualUpdateOnly = () => setManualUpdateOnly(true);
    const handleForceManualUpdatePrompt = () => setForceManualUpdatePrompt(true);

    ipcRenderer.on("manual-update-only", handleManualUpdateOnly);
    ipcRenderer.on("force-manual-update-prompt", handleForceManualUpdatePrompt);
    ipcRenderer.invoke("is-manual-update-only").then(setManualUpdateOnly);

    return () => {
      ipcRenderer.removeListener("manual-update-only", handleManualUpdateOnly);
      ipcRenderer.removeListener("force-manual-update-prompt", handleForceManualUpdatePrompt);
    };
  }, []);

  useEffect(() => {
    (async () => {
      const status: UpdateStatus = await ipcRenderer.invoke("get-last-update-status").catch((): null => null);
      if (status) {
        await ipcRenderer.invoke("acknowledge-update-status").catch((): null => null);
        await reportLastUpdate(status);
      }
      setLastUpdateReported(true);
    })();
  }, []);

  useEffect(() => {
    if (state === "downloaded") {
      Swal.fire({
        icon: "success",
        text: t("update_restart"),
        showCancelButton: true
      }).then(({ value }) => value && ipcRenderer.send("reboot-after-download"));
    }
  }, [state]);

  useEffect(() => {
    if (!hasManualUpdate || manualUpdatePromptShown || !lastUpdateReported) {
      return;
    }

    setManualUpdatePromptShown(true);
    Swal.fire({
      icon: "info",
      title: t("updateAvailableTitle"),
      text: t("updateAvailableManual").replace("{currentVersion}", currentVersion).replace("{latestVersion}", latestVersion),
      showCancelButton: true,
      confirmButtonText: t("updateNow"),
      cancelButtonText: t("updateLater"),
      allowOutsideClick: false
    }).then(async ({ isConfirmed }) => {
      if (!isConfirmed) return;

      Swal.fire({
        title: t("updateInProgressTitle"),
        html: `<div id="${PROGRESS_ELEMENT_ID}">${escapeHtml(t("updatePreparing"))}</div>`,
        allowOutsideClick: false,
        allowEscapeKey: false,
        didOpen: () => Swal.showLoading()
      });

      const handleProgress = (_: unknown, progress: UpdateProgress) => {
        const element = document.getElementById(PROGRESS_ELEMENT_ID);
        if (element) {
          element.textContent = describeProgress(progress);
        }
      };

      ipcRenderer.on("update-progress", handleProgress);
      let result: UpdateResult;
      try {
        result = await ipcRenderer.invoke("install-manual-update");
      } catch (error) {
        result = { ok: false, error: error instanceof Error ? error.message : String(error) };
      } finally {
        ipcRenderer.removeListener("update-progress", handleProgress);
      }

      if (!result?.ok) {
        await showFailure(result?.error || t("FETCH_FAILED"), null, result?.logPath);
      } else if (result.openedReleasePage) {
        Swal.close();
      }
    });
  }, [hasManualUpdate, currentVersion, latestVersion, manualUpdatePromptShown, lastUpdateReported]);

  if ((process.platform !== "win32" || manualUpdateOnly) && hasManualUpdate) {
    return <Box p={2} pb={0}>
      <Alert severity="info">
        You have version v{currentVersion}, please consider updating to the latest version from <a href="https://github.com/Casual-Gamer8001/RyuSAK/releases/latest" target="_blank">GitHub</a> (v{ latestVersion })
      </Alert>
    </Box>;
  }

  if (!state) {
    return null;
  }

  return (
    <Box p={2} pb={0}>
      <Alert severity="info">{ t(state === "downloading" ? "update_downloading" : "update_downloaded") }</Alert>
    </Box>
  );
};

export default UpdateComponent;
