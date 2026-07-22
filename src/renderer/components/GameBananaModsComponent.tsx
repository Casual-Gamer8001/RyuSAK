import React, { useEffect, useState } from "react";
import { GameBananaFile, GameBananaMod } from "../../types";
import { Alert, Box, Button, Divider, Grid, Tooltip, Typography } from "@mui/material";
import { styled } from "@mui/material/styles";
import Paper from "@mui/material/Paper";
import useTranslation from "../i18n/I18nService";
import { invokeIpc } from "../utils";
import Swal from "sweetalert2";

const Label = styled(Paper)(({ theme }) => ({
  ...theme.typography.body2,
  border: "1px solid black",
  borderBottomLeftRadius: 0,
  borderBottomRightRadius: 0,
  alignItems: "center",
  justifyContent: "center",
  padding: "8px 8px",
  color: "#FFF",
  zIndex: 1,
  lineHeight: 1.5,
  height: "3em",
  whiteSpace: "nowrap",
  overflow: "hidden",
  textOverflow: "ellipsis",
  display: "block",
  textAlign: "center"
}));

const GameBanaCover = styled(Box)(() => ({
  width: "100%",
  aspectRatio: "16 / 9",
  background: "no-repeat center center",
  backgroundSize: "contain"
}));

type Props = {
  titleId?: string,
  titleName?: string,
  dataPath?: string,
};

const formatBytes = (bytes: number) => {
  if (!Number.isFinite(bytes) || bytes <= 0) return "";
  const units = ["B", "KB", "MB", "GB"];
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / Math.pow(1024, index)).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
};

const GameBananaModsComponent = ({ titleId, titleName, dataPath }: Props) => {
  const [gameBananaMods, setGameBananaMods] = useState<Array<GameBananaMod>>(null);
  const [loading, setLoading] = useState(true);
  const [installingModId, setInstallingModId] = useState<number>(null);
  const { t } = useTranslation();

  useEffect(() => {
    if (titleName) {
      invokeIpc("search-gamebanana", titleName).then(mods => {
        setGameBananaMods(mods || []);
        setLoading(false);
      });
    }
  }, [titleName]);

  const installMod = async (mod: GameBananaMod) => {
    if (!titleId || !dataPath) return;

    setInstallingModId(mod.id);
    try {
      const files = await invokeIpc("get-gamebanana-mod-files", mod.id) as GameBananaFile[];
      if (!files || files.length === 0) {
        await Swal.fire({ icon: "error", text: t("gamebananaNoFiles") });
        return;
      }

      let selectedFile = files[0];
      if (files.length > 1) {
        const inputOptions = files.reduce<Record<string, string>>((acc, file) => ({
          ...acc,
          [file.id]: `${file.name}${file.size ? ` (${formatBytes(file.size)})` : ""}`
        }), {});
        const result = await Swal.fire({
          title: t("gamebananaPickFile"),
          input: "select",
          inputOptions,
          inputValue: `${selectedFile.id}`,
          showCancelButton: true,
          confirmButtonText: t("continue"),
          cancelButtonText: t("cancel")
        });

        if (!result.isConfirmed) return;
        selectedFile = files.find(file => `${file.id}` === result.value) || selectedFile;
      }

      const destination = `${dataPath}\\mods\\contents\\${titleId.toLowerCase()}\\`;
      const confirmation = await Swal.fire({
        icon: "question",
        title: t("gamebananaInstallTitle"),
        html: t("gamebananaInstallConfirm")
          .replace("{file}", selectedFile.name)
          .replace("{path}", destination),
        showCancelButton: true,
        confirmButtonText: t("gamebananaInstall"),
        cancelButtonText: t("cancel")
      });

      if (!confirmation.isConfirmed) return;

      Swal.fire({
        title: t("gamebananaInstalling"),
        allowOutsideClick: false,
        didOpen: () => Swal.showLoading()
      });

      const installPath = await invokeIpc("install-gamebanana-mod", titleId, mod.id, selectedFile.id, dataPath);
      await Swal.fire({
        icon: "success",
        html: t("gamebananaInstalled").replace("{path}", installPath)
      });
    } catch (error) {
      await Swal.fire({
        icon: "error",
        title: t("gamebananaInstallFailed"),
        text: error instanceof Error ? error.message : String(error)
      });
    } finally {
      setInstallingModId(null);
    }
  };

  return <>
    <h3>{t("gamebananaModsTitle")}</h3>
    <Divider />
    <br />
    {
      loading
        ? <Box pt={16} style={{ position: "relative" }}>
            <div className="centred-container">
              <div className="ripple-container">
                <span className="loading-ripple" />
              </div>
              <Typography variant="h6">{t("gameBananaLoading")}</Typography>
            </div>
          </Box>
        : <Box pb={3}>
          {
            gameBananaMods.length === 0
              ? <Alert severity="info">{t("gamebananaNoMods")}</Alert>
              : <Grid container spacing={2}>
                {
                   (gameBananaMods).map(mod => (
                     <Grid key={mod.name} item xs={2}>
                       <Tooltip arrow title={mod.name} placement="top">
                         <Box>
                           <a style={{ textDecoration: "none", color: "#FFF" }} href={mod.url} className="no-blank-icon" target="_blank">
                             <Label title={mod.name}>{mod.name}</Label>
                             <GameBanaCover style={{ backgroundImage: mod.cover ? `url(${mod.cover})` : undefined }} />
                           </a>
                           <Button
                             size="small"
                             variant="contained"
                             fullWidth
                             disabled={!mod.hasFiles || installingModId === mod.id}
                             onClick={() => installMod(mod)}
                           >
                             {t("gamebananaInstall")}
                           </Button>
                         </Box>
                       </Tooltip>
                     </Grid>
                   ))
                }
              </Grid>
          }
          </Box>
    }
  </>;
};

export default GameBananaModsComponent;
