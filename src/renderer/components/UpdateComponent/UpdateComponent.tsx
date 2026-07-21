import React, { useEffect, useState } from "react";
import { Alert, Box } from "@mui/material";
import useTranslation from "../../i18n/I18nService";
import Swal from "sweetalert2";
import { ipcRenderer } from "electron";
import useStore from "../../actions/state";
import semver from "semver";

const UpdateComponent = ({ state }: { state: "downloading" | "downloaded" }) => {
  const [latestVersion, currentVersion] = useStore(state => [state.latestVersion, state.currentVersion]);
  const [manualUpdateOnly, setManualUpdateOnly] = useState(false);
  const [manualUpdatePromptShown, setManualUpdatePromptShown] = useState(false);
  const forceManualUpdatePrompt = process.env.RYUSAK_FORCE_UPDATE_PROMPT === "1";
  const { t } = useTranslation();
  const hasManualUpdate = manualUpdateOnly
    && currentVersion
    && latestVersion
    && (forceManualUpdatePrompt || semver.lt(currentVersion, latestVersion));

  useEffect(() => {
    ipcRenderer.on("manual-update-only", () => setManualUpdateOnly(true));
    ipcRenderer.invoke("is-manual-update-only").then(setManualUpdateOnly);
  }, [manualUpdateOnly]);

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
    if (!hasManualUpdate || manualUpdatePromptShown) {
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
        title: t("update_downloading"),
        allowOutsideClick: false,
        didOpen: () => Swal.showLoading()
      });

      const result = await ipcRenderer.invoke("install-manual-update");
      if (result?.ok === false) {
        Swal.fire({
          icon: "error",
          title: t("updateFailed"),
          text: result.error || t("FETCH_FAILED")
        });
      }
    });
  }, [hasManualUpdate, currentVersion, latestVersion, manualUpdatePromptShown]);

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
