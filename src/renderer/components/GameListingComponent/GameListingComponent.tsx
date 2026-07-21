import React, { useEffect, useState } from "react";
import Stack from "@mui/material/Stack";
import Paper from "@mui/material/Paper";
import { styled } from "@mui/material/styles";
import "./gameListing.css";
import { RyujinxConfigMeta, EShopTitleMeta, LS_KEYS } from "../../../types";
import useStore from "../../actions/state";
import { Box, Button, Divider, Grid, TextField, Tooltip } from "@mui/material";
import jackSober from "../../resources/jack_sober.png";
import useTranslation from "../../i18n/I18nService";
import RefreshIcon from "@mui/icons-material/Refresh";
import { useNavigate } from "react-router-dom";
import { invokeIpc } from "../../utils";

interface IConfigContainer {
  config: RyujinxConfigMeta;
}

const nonAlphaNumeric = new RegExp(/[^a-z0-9\s]/g);
const coverRepositoryUrl = "https://ryusak-shader-backend.azurewebsites.net/covers";

const getJsonLocalStorage = <T,>(key: LS_KEYS, fallback: T): T => {
  try {
    return JSON.parse(localStorage.getItem(key)) || fallback;
  } catch {
    return fallback;
  }
};

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
  whiteSpace: "nowrap",
  overflow: "hidden",
  textOverflow: "ellipsis",
  display: "block",
  textAlign: "center"
}));

const Cover = styled(Box)(() => ({
  width: "100%",
  aspectRatio: "1 / 1",
  backgroundColor: "#444",
  backgroundSize: "cover",
}));

const gameIconSizeValues = [90, 95, 105, 120, 135, 155, 180, 210, 245, 285, 330, 360];

const closestGameIconSize = (value: number) => gameIconSizeValues
  .reduce((closest, size) => Math.abs(size - value) < Math.abs(closest - value) ? size : closest, gameIconSizeValues[0]);

const gameIconSizeValue = (value: string) => {
  const legacySizes: { [key: string]: number } = {
    extraSmall: 90,
    small: 180,
    medium: 270,
    large: 360,
  };
  const numericValue = Number(value);
  return Number.isFinite(numericValue) ? closestGameIconSize(numericValue) : legacySizes[value] || 180;
};

const GameListingComponent = ({ config }: IConfigContainer) => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [openAlertAction] = useStore(s => [s.openAlertAction]);
  const [settings] = useStore(s => [s.settings]);
  const [games, setGames] = useState<EShopTitleMeta[]>([]);
  const [coverUrls, setCoverUrls] = useState<{ [key: string]: string }>({});
  const [isLoaded, setIsLoaded] = useState(false);
  const [searchTerm, setSearchTerm] = React.useState("");
  const [filteredGames, setFilteredGames] = useState<typeof games>([]);
  const [showHiddenGames, setShowHiddenGames] = useState(false);
  const [hiddenGames, setHiddenGames] = useState<string[]>([]);
  const gameIconSize = gameIconSizeValue(settings.gameIconSize || "180");

  // 1. Scan games on user system
  // 2. Build metadata from eshop with titleId as argument
  const createLibrary = async () => {
    let titleIds = await invokeIpc("scan-games", config.path);
    titleIds = titleIds.filter(id => id !== "0000000000000000"); // Homebrew app
    const currentHiddenGames = getJsonLocalStorage<string[]>(LS_KEYS.HIDDEN_GAMES, []);
    setHiddenGames(currentHiddenGames);
    titleIds = showHiddenGames ? titleIds : titleIds.filter(id => !currentHiddenGames.includes(id.toUpperCase()));

    const gamesCollection: EShopTitleMeta[] = await Promise.all(titleIds.map(async (i: string) => invokeIpc("build-metadata-from-titleId", i, config.path)));
    gamesCollection.forEach(title => title.normalizedName = title.name.toLowerCase().normalize("NFKD").replace(nonAlphaNumeric, ""));

    setGames(gamesCollection);
  };

  useEffect(() => {
    createLibrary().catch(() => setIsLoaded(true));
  }, [config, showHiddenGames]);

  useEffect(() => {
    const searchTermLowerCase = searchTerm.toLowerCase();
    setFilteredGames(searchTerm.length > 0
      ? games.filter(title => title.normalizedName.includes(searchTermLowerCase))
      : games);
    setIsLoaded(true);
  }, [games, searchTerm]);

  useEffect(() => {
    const coverOverrides = getJsonLocalStorage<{ [key: string]: string }>(LS_KEYS.COVER_OVERRIDES, {});
    const coverCache = getJsonLocalStorage<{ [key: string]: string }>(LS_KEYS.COVER_CACHE, {});
    const nextCoverUrls: { [key: string]: string } = {};
    const steamGridDbApiKey = settings.steamGridDbApiKey;

    games.forEach(game => {
      const existingCoverUrl = game.iconUrl?.length > 0 && !game.iconUrl.includes("image-not-found")
        ? game.iconUrl
        : null;
      nextCoverUrls[game.id] = coverOverrides[game.id]
        || coverCache[game.id]
        || existingCoverUrl
        || `${coverRepositoryUrl}/${game.id}.svg?name=${encodeURIComponent(game.name)}`;
    });
    setCoverUrls(nextCoverUrls);

    if (!steamGridDbApiKey) return;

    games
      .filter(game => !coverOverrides[game.id] && !coverCache[game.id] && (!game.iconUrl || game.iconUrl.includes("image-not-found")))
      .forEach(async game => {
        const searchResult = await invokeIpc("search-steamgriddb-covers", game.name) as { covers: Array<{ url: string }> };
        const grid = (searchResult.covers || [])[0];
        if (!grid) return;

        const updatedCoverCache = getJsonLocalStorage<{ [key: string]: string }>(LS_KEYS.COVER_CACHE, {});
        updatedCoverCache[game.id] = grid.url;
        localStorage.setItem(LS_KEYS.COVER_CACHE, JSON.stringify(updatedCoverCache));
        setCoverUrls(current => ({ ...current, [game.id]: grid.url }));
      });
  }, [games, settings.steamGridDbApiKey]);

  const refreshLibrary = () => {
    openAlertAction("info", t("refreshInfo"));
    return createLibrary();
  };

  const onGameDetailClick = (titleId: string) => {
    navigate("/detail", { state: { titleId, dataPath: config.path } });
  };

  if ((games.length === 0 || filteredGames.length === 0 || !isLoaded) && searchTerm.length === 0) {
    return (
      <div style={{ textAlign: "center", width: "50%", margin: "0 auto" }}>
        <p>
          <img width="100%" src={jackSober} alt="" />
        </p>
        <Divider />
        <h4 dangerouslySetInnerHTML={{ __html: t("launchRyujinx") }} />
        <p style={{ textAlign: "center" }}>
          <Button onClick={refreshLibrary} startIcon={<RefreshIcon />} variant="outlined">{t("refresh")}</Button>
        </p>
      </div>
    );
  }

  return (
    <Stack className="masonry" spacing={2}>
      <Grid container>
        <Grid item xs={8} pr={2}>
          <TextField type="search" variant="standard" label={t("filter").replace("{{LENGTH}}", `${games.length}`)} value={searchTerm} onChange={e => setSearchTerm(e.target.value)} fullWidth />
        </Grid>
        <Grid item xs={2} pr={2}>
          <Button onClick={() => setShowHiddenGames(!showHiddenGames)} variant={showHiddenGames ? "contained" : "outlined"} fullWidth>{showHiddenGames ? t("hideHiddenGames") : t("showHiddenGames")}</Button>
        </Grid>
        <Grid item xs={2}>
          <Button onClick={refreshLibrary} startIcon={<RefreshIcon />} variant="outlined" fullWidth>{t("refresh")}</Button>
        </Grid>
      </Grid>
      <Box
        pr={4}
        sx={{
          display: "grid",
          gap: 2,
          gridTemplateColumns: `repeat(auto-fill, minmax(${gameIconSize}px, 1fr))`,
        }}
      >
        {
          filteredGames
            .sort((a, b) => a.name.localeCompare(b.name))
            .map((item, index) => (
              <Box tabIndex={index} className="game" onClick={() => onGameDetailClick(item.id)} sx={{ cursor: "pointer", justifySelf: "center", maxWidth: `${gameIconSize + 40}px`, width: "100%" }} key={index}>
                <Tooltip arrow placement="top" title={item.name}>
                  <div>
                    <Label>{hiddenGames.includes(item.id.toUpperCase()) ? `${item.name} (${t("hidden")})` : item.name}</Label>
                    <Cover style={{ opacity: hiddenGames.includes(item.id.toUpperCase()) ? 0.45 : 1, backgroundImage: `url(${coverUrls[item.id] || `${coverRepositoryUrl}/${item.id}.svg?name=${encodeURIComponent(item.name)}`})` }} />
                  </div>
                </Tooltip>
              </Box>
            ))
        }
      </Box>
    </Stack>
  );
};

export default GameListingComponent;
