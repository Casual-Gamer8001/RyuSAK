import React, { useEffect, useState } from "react";
import {
  Alert,
  Box,
  Button,
  Chip,
  Divider,
  Grid,
  IconButton,
  Tooltip, Typography
} from "@mui/material";
import ArrowBackIcon from "@mui/icons-material/ArrowBack";
import useStore from "../../actions/state";
import { shell } from "electron";
import useTranslation from "../../i18n/I18nService";
import { GithubIssue, GithubLabel, EShopTitleMeta, LS_KEYS } from "../../../types";
import Swal from "sweetalert2";
import InfoIcon from "@mui/icons-material/Info";
import defaultIcon from "../../resources/default_icon.jpg";
import { styled } from "@mui/material/styles";
import MuiGrid from "@mui/material/Grid";
import GameBananaModsComponent from "../GameBananaModsComponent";
import DeleteIcon from "@mui/icons-material/Delete";
import { useLocation, useNavigate } from "react-router-dom";
import { invokeIpc } from "../../utils";

interface IGameDetailProps {
  titleId: string;
  dataPath: string;
}

const GridWithVerticalSeparator = styled(MuiGrid)(({ theme }) => ({
  width: "100%",
  ...theme.typography.body2,
  "& [role=\"separator\"]": {
    margin: theme.spacing(0, 2),
  },
}));

// Force title to be two lines, so it's always aligned even there is long strings depending on locale
const TwoLinesTitle = styled(Typography)(() => ({
  lineHeight: "1.5em",
  height: "3em",
  overflow: "hidden"
}));

const coverRepositoryUrl = "https://ryusak-shader-backend.azurewebsites.net/covers";

const getJsonLocalStorage = <T,>(key: LS_KEYS, fallback: T): T => {
  try {
    return JSON.parse(localStorage.getItem(key)) || fallback;
  } catch {
    return fallback;
  }
};

type SteamGridDbGrid = {
  id: number;
  url: string;
  thumb?: string;
};

type SteamGridDbCoverSearchResult = {
  error?: "MISSING_API_KEY" | "API_KEY_INVALID" | "REQUEST_FAILED";
  covers: SteamGridDbGrid[];
};

const GameDetailComponent = () => {
  const { state } = useLocation();
  const { titleId, dataPath } = state as IGameDetailProps;
  const [
    ryujinxShaders,
    ryujinxShaderVariants,
    downloadShadersAction,
    needRefreshShaders,
    shareShaders,
    refreshShaderIndexAction,
    deleteGameAction,
    threshold,
    shadersMinVersion,
    settings
  ] = useStore(state => [
    state.ryujinxShaders,
    state.ryujinxShaderVariants,
    state.downloadShadersAction,
    state.needRefreshShaders,
    state.shareShaders,
    state.refreshShaderIndexAction,
    state.deleteGameAction,
    state.threshold,
    state.shadersMinVersion,
    state.settings
  ]);
  const [metaData, setMetaData]: [EShopTitleMeta, Function] = useState(null);
  const [compat, setCompat] = useState<GithubLabel[]>(null);
  const [_compatMode, setCompatMode] = useState<GithubIssue["mode"]>(null);
  const [localShadersCount, setLocalShadersCount] = useState(0);
  const [localShaderCacheKey, setLocalShaderCacheKey] = useState<string>(null);
  const [customCoverUrl, setCustomCoverUrl] = useState("");
  const { t } = useTranslation();
  const navigate = useNavigate();

  const extractCompatibilityLabels = (response: GithubIssue) => {
    // Probably non 200 response from GitHub, so leave it as default value (null)
    if (response == null) return;

    const item = (response.items).find(i => i.state === "open");
    setCompatMode(response.mode);
    return setCompat(item ? item.labels : []);
  };

  const handleAddReportButtonClick = async () => {
    if (compat === null) return;

    await Swal.fire({
      icon: "info",
      text: t(compat.length === 0 ? "infoNewReport" : "infoExistingReport"),
      allowOutsideClick: false
    });

    return shell.openExternal(compat.length > 0
      ? `https://github.com/Ryubing/Compatibility/issues?q=is%3Aissue+is%3Aopen+${encodeURIComponent(_compatMode === "name" ? metaData.name : metaData.id)}`
      : "https://github.com/Ryubing/Compatibility/issues/new/choose"
    );
  };

  useEffect(() => {
    invokeIpc("build-metadata-from-titleId", titleId, dataPath).then(d => setMetaData(d));
    refreshShaderIndexAction();
    invokeIpc("getRyujinxCompatibility", titleId).then(extractCompatibilityLabels);
    invokeIpc("count-shaders", titleId, dataPath, shadersMinVersion).then(setLocalShadersCount);
    invokeIpc("get-shader-cache-key", titleId, dataPath).then(setLocalShaderCacheKey);
    setCustomCoverUrl(getJsonLocalStorage<{ [key: string]: string }>(LS_KEYS.COVER_OVERRIDES, {})[titleId] || "");
  }, [titleId, needRefreshShaders]);

  const getCoverUrl = () => customCoverUrl
    || (metaData?.iconUrl?.length > 0 && !metaData.iconUrl.includes("image-not-found") ? metaData.iconUrl : null)
    || `${coverRepositoryUrl}/${metaData.id}.svg?name=${encodeURIComponent(metaData.name)}`;

  const setCustomCover = async () => {
    const result = await Swal.fire({
      title: t("customCoverTitle"),
      text: t("customCoverDescription"),
      input: "text",
      inputValue: customCoverUrl,
      showCancelButton: true,
      confirmButtonText: t("save"),
      cancelButtonText: t("cancel"),
    });

    if (!result.isConfirmed) return;

    const value = `${result.value || ""}`.trim();
    const coverOverrides = getJsonLocalStorage<{ [key: string]: string }>(LS_KEYS.COVER_OVERRIDES, {});

    if (value) {
      coverOverrides[metaData.id] = value;
    } else {
      delete coverOverrides[metaData.id];
    }

    localStorage.setItem(LS_KEYS.COVER_OVERRIDES, JSON.stringify(coverOverrides));
    setCustomCoverUrl(value);
  };

  const clearCustomCover = () => {
    const coverOverrides = getJsonLocalStorage<{ [key: string]: string }>(LS_KEYS.COVER_OVERRIDES, {});
    delete coverOverrides[metaData.id];
    localStorage.setItem(LS_KEYS.COVER_OVERRIDES, JSON.stringify(coverOverrides));
    setCustomCoverUrl("");
  };

  const setCustomTitle = async () => {
    const result = await Swal.fire({
      title: t("customTitleTitle"),
      text: t("customTitleDescription"),
      input: "text",
      inputValue: metaData.name,
      showCancelButton: true,
      confirmButtonText: t("save"),
      cancelButtonText: t("cancel"),
    });

    if (!result.isConfirmed) return;

    const value = `${result.value || ""}`.trim();
    const updatedMeta = await invokeIpc("set-title-override", metaData.id, value);
    setMetaData(updatedMeta);
  };

  const hideGame = async () => {
    const result = await Swal.fire({
      icon: "warning",
      title: t("hideGame"),
      text: t("hideGameDescription"),
      showCancelButton: true,
      confirmButtonText: t("hideGame"),
      cancelButtonText: t("cancel"),
    });

    if (!result.isConfirmed) return;

    const hiddenGames = getJsonLocalStorage<string[]>(LS_KEYS.HIDDEN_GAMES, []);
    const titleId = metaData.id.toUpperCase();
    if (!hiddenGames.includes(titleId)) {
      hiddenGames.push(titleId);
      localStorage.setItem(LS_KEYS.HIDDEN_GAMES, JSON.stringify(hiddenGames));
    }

    navigate(-1);
  };

  const unhideAllGames = () => {
    localStorage.setItem(LS_KEYS.HIDDEN_GAMES, JSON.stringify([]));
    Swal.fire({ icon: "success", text: t("hiddenGamesCleared") });
  };

  const chooseSteamGridDbCover = async () => {
    const steamGridDbApiKey = settings.steamGridDbApiKey;

    if (!steamGridDbApiKey) {
      await Swal.fire({ icon: "error", text: t("steamGridDbApiKeyRequired") });
      return;
    }

    const searchResult = await invokeIpc("search-steamgriddb-covers", metaData.name) as SteamGridDbCoverSearchResult;

    if (searchResult.error) {
      await Swal.fire({
        icon: "error",
        text: searchResult.error === "MISSING_API_KEY"
          ? t("steamGridDbApiKeyRequired")
          : searchResult.error === "API_KEY_INVALID"
            ? t("steamGridDbApiKeyInvalid")
            : t("steamGridDbSearchFailed")
      });
      return;
    }

    const grids = searchResult.covers || [];

    if (grids.length === 0) {
      await Swal.fire({ icon: "error", text: t("steamGridDbNoCovers") });
      return;
    }

    const result = await Swal.fire({
      title: t("chooseSteamGridDbCover"),
      html: `
        <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:10px;max-height:520px;overflow:auto;">
          ${grids.map((grid, index) => `
            <label style="cursor:pointer;border:1px solid #555;padding:6px;border-radius:4px;">
              <input type="radio" name="sgdb-cover" value="${grid.url}" ${index === 0 ? "checked" : ""} />
              <img src="${grid.thumb || grid.url}" style="width:100%;display:block;margin-top:6px;" />
            </label>
          `).join("")}
        </div>
      `,
      showCancelButton: true,
      confirmButtonText: t("save"),
      cancelButtonText: t("cancel"),
      preConfirm: () => {
        const selected = document.querySelector<HTMLInputElement>("input[name='sgdb-cover']:checked");
        return selected?.value;
      },
    });

    if (!result.isConfirmed || !result.value) return;

    const coverOverrides = getJsonLocalStorage<{ [key: string]: string }>(LS_KEYS.COVER_OVERRIDES, {});
    coverOverrides[metaData.id] = result.value;
    localStorage.setItem(LS_KEYS.COVER_OVERRIDES, JSON.stringify(coverOverrides));
    setCustomCoverUrl(result.value);
  };

  const renderCompatibilityData = () => (
    <Grid container mb={2} sx={{ display: "flex", alignItems: "center" }}>
      <Grid item xs={12}>
        <Alert
          severity={compat.length === 0 ? "warning" : "info"}
          action={(<Button
            onClick={() => handleAddReportButtonClick()}
            variant="outlined"
            size="small"
            fullWidth
          >
            {t("addCompatReport")}
          </Button>)}
        >
          {
            compat.map(c => (
              <Tooltip key={c.name} title={c.description} arrow enterDelay={0}>
                <Chip variant="outlined" color="primary" size="small" style={{ marginRight: 8 }} label={c.name} />
              </Tooltip>
            ))
          }
          {
            compat.length === 0 && t("noCompatData")
          }
        </Alert>
      </Grid>
    </Grid>
  );

  if (!metaData) {
    return null;
  }

  const matchingShaderVariant = localShaderCacheKey ? ryujinxShaderVariants?.[metaData.id]?.[localShaderCacheKey] : null;
  const ryusakShadersCount = matchingShaderVariant?.shaderCount || ryujinxShaders[metaData.id] || 0;
  const ryusakShadersPath = matchingShaderVariant?.path;

  return (
    <Box p={3}>
      <Box sx={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
        <Button onClick={() => navigate(-1)} size="small" variant="outlined"><ArrowBackIcon /></Button>
          {
            metaData && (
              <h3 style={{ marginLeft: 12 }}>{metaData.name}</h3>
            )
          }
        <Button
          variant="contained"
          color="error"
          onClick={() => deleteGameAction(metaData.id, dataPath).then(confirmed => confirmed && navigate(-1))}
          startIcon={<DeleteIcon />}
        >
          {t("deleteGame")}
        </Button>
      </Box>

      <Divider />
      <br />

      <div style={{ height: 70 }}>
        {
          (compat !== null)
            ? renderCompatibilityData()
            : (<Alert severity="info">Loading ...</Alert>)
        }
      </div>

      <Grid container mt={0}>
        <Grid item xs={2}>
          <img
            referrerPolicy="no-referrer"
            style={{ border: "5px solid #222" }}
            width="100%" src={getCoverUrl() || defaultIcon}
            alt=""
          />
          <p>
            <Button onClick={setCustomCover} variant="contained" fullWidth>{t("setCustomCover")}</Button>
          </p>
          <p>
            <Button onClick={chooseSteamGridDbCover} variant="contained" fullWidth>{t("chooseSteamGridDbCover")}</Button>
          </p>
          <p>
            <Button onClick={clearCustomCover} variant="outlined" disabled={!customCoverUrl} fullWidth>{t("clearCustomCover")}</Button>
          </p>
          <p>
            <Button onClick={setCustomTitle} variant="outlined" fullWidth>{t("setCustomTitle")}</Button>
          </p>
          <p>
            <Button onClick={hideGame} variant="outlined" color="warning" fullWidth>{t("hideGame")}</Button>
          </p>
          <p>
            <Button onClick={unhideAllGames} variant="outlined" fullWidth>{t("unhideAllGames")}</Button>
          </p>
        </Grid>
        <Grid item xs={4} p={1} pl={2}>
          <p style={{ marginTop: 0 }}>
            <Button
              onClick={() => invokeIpc("openFolderForGame", titleId, "shaders", dataPath)}
              variant="contained"
              fullWidth
            >
              {t("openShaderDir")}
            </Button>
          </p>
          <p>
            <Button
              onClick={() => invokeIpc("openFolderForGame", titleId, "mods", dataPath)}
              variant="contained"
              fullWidth
            >
              {t("openModsDir")}
            </Button>
          </p>
        </Grid>
        <Grid item xs={6} pl={3} pr={3} style={{ position: "relative", top: -10 }}>
          <Grid container>
            <Grid item xs={3}>
              <h3 style={{ margin: "0 auto" }}>
                <Tooltip placement="right" title={(<div dangerouslySetInnerHTML={{ __html: t("shaderInfo") }} />)}>
                  <IconButton style={{ position: "relative", top: -3 }} size="small" color="primary">
                    <InfoIcon />
                  </IconButton>
                </Tooltip>
                {t("shaders")}
              </h3>
            </Grid>

            <Grid item xs={9}>

              <h3 style={{ margin: "0 auto", textAlign: "right" }}>
                {t("threshold")}
                <Tooltip placement="right" title={(<div dangerouslySetInnerHTML={{ __html: t("shaderThreshold") }} />)}>
                  <IconButton style={{ position: "relative", top: -3 }} size="small" color="primary">
                    <InfoIcon />
                  </IconButton>
                </Tooltip>
                <code style={{ position: "relative", top: -3 }}>{threshold}</code>
              </h3>
            </Grid>

            <Grid item xs={12}>
              <Divider />
            </Grid>
          </Grid>
          <GridWithVerticalSeparator container pt={2} spacing={0}>
            <GridWithVerticalSeparator item xs pr={2}>
              <Box>
                <TwoLinesTitle variant="h6" align="center">{t("localShadersCount")}</TwoLinesTitle>
                <p><Button style={{ pointerEvents: "none" }} variant="outlined" fullWidth>{localShadersCount}</Button></p>
                <p>
                  <Button
                    variant="contained"
                    fullWidth
                    disabled={threshold == -1 ? true : ((ryusakShadersCount + threshold) >= localShadersCount)}
                    onClick={() => shareShaders(metaData.id, dataPath, localShadersCount, ryusakShadersCount)}
                  >
                    {threshold == -1 ? t("shaderUploadingUnavailable") : t("shareShaders")}
                  </Button>
                </p>
              </Box>
            </GridWithVerticalSeparator>

            <Divider flexItem orientation="vertical" />

            <GridWithVerticalSeparator item xs pl={2}>
              <Box>
                <TwoLinesTitle variant="h6" align="center">{t("ryusakShadersCount")}</TwoLinesTitle>
                <p>
                  <Button style={{ pointerEvents: "none" }} variant="outlined" fullWidth>
                    {ryusakShadersCount}
                  </Button>
                </p>
                <p>
                  <Button
                    variant="contained"
                    fullWidth
                    disabled={ryusakShadersCount === 0}
                    onClick={() => downloadShadersAction(metaData.id, dataPath, ryusakShadersPath)}
                  >
                    {t("dlShaders")}
                  </Button>
                </p>
              </Box>
            </GridWithVerticalSeparator>
          </GridWithVerticalSeparator>
        </Grid>

        <Grid item xs={12}>
          <GameBananaModsComponent titleId={metaData?.id} titleName={metaData?.name} dataPath={dataPath} />
        </Grid>
      </Grid>
    </Box>
  );
};

export default GameDetailComponent;
