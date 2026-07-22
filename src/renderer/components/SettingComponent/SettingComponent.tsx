import "./setting.css";
import React, { useState } from "react";
import {
  Box,
  Button,
  TextField,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Select,
  MenuItem,
  InputLabel,
  FormControl,
  Link,
  Slider,
  Typography
} from "@mui/material";
import useTranslation from "../../i18n/I18nService";
import useStore from "../../actions/state";
import { LS_KEYS } from "../../../types";
import { LANGUAGES } from "../../app";

const gameIconSizeMarks = [90, 95, 105, 120, 135, 155, 180, 210, 245, 285, 330, 360];
const gameIconSizeLabelKeys = [
  "gameIconSizeCompact",
  "gameIconSizeCompact",
  "gameIconSizeSmall",
  "gameIconSizeSmall",
  "gameIconSizeMedium",
  "gameIconSizeMedium",
  "gameIconSizeLarge",
  "gameIconSizeLarge",
  "gameIconSizeExtraLarge",
  "gameIconSizeExtraLarge",
  "gameIconSizeShowcase",
  "gameIconSizeShowcase"
] as const;
const gameIconSizeNamedMarks = [0, 2, 4, 6, 8, 10].map(value => ({
  value,
  labelKey: gameIconSizeLabelKeys[value]
}));

const closestGameIconSize = (value: number) => gameIconSizeMarks
  .reduce((closest, size) => Math.abs(size - value) < Math.abs(closest - value) ? size : closest, gameIconSizeMarks[0]);

const gameIconSizeIndex = (value: number) => gameIconSizeMarks.indexOf(closestGameIconSize(value));

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

const gameIconSizeLabelKey = (value: number) => gameIconSizeLabelKeys[gameIconSizeIndex(value)];

const SettingComponent = () => {
  const { t } = useTranslation();
  const [
    settings,
    setProxyAction,
    setSteamGridDbApiKeyAction,
    setGameIconSizeAction,
    setLocaleAction
  ] = useStore(state => [
    state.settings,
    state.setProxyAction,
    state.setSteamGridDbApiKeyAction,
    state.setGameIconSizeAction,
    state.setLocaleAction
  ]);

  const [open, setOpen] = useState(false);
  const [proxy, setProxy] = useState(settings.proxy);
  const [steamGridDbApiKey, setSteamGridDbApiKey] = useState(settings.steamGridDbApiKey || "");
  const [gameIconSize, setGameIconSize] = useState(gameIconSizeValue(settings.gameIconSize || "180"));
  const [locale, setLocale] = useState(localStorage.getItem(LS_KEYS.LOCALE) ?? "en");

  const handleClose = () =>
    setOpen(false);

  const handleSave = async () => {
    await setProxyAction(proxy);
    await setSteamGridDbApiKeyAction(steamGridDbApiKey);
    await setGameIconSizeAction(`${gameIconSize}`);
    await setLocaleAction(locale);

    setOpen(false);
  };

  return <>
    <Button style={{ fill: "#fff" }} onClick={() => setOpen(!open)}>
      <svg width={32} viewBox="0 0 24 24">
        <path d="M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z"></path>
      </svg>
    </Button>
    <Dialog open={open} fullWidth onClose={handleClose} className="setting-dialog">
      <DialogTitle>{t("settings")}</DialogTitle>
      <DialogContent>
        <TextField sx={{ mt: 1 }} label={t("proxy")} value={proxy} onChange={(e) => setProxy(e.target.value)} fullWidth />
        <TextField sx={{ mt: 3 }} label={t("steamGridDbApiKey")} value={steamGridDbApiKey} onChange={(e) => setSteamGridDbApiKey(e.target.value)} fullWidth />
        <Typography sx={{ mt: 1 }} variant="body2">
          {t("steamGridDbApiKeyHelp")}{" "}
          <Link href="https://www.steamgriddb.com/profile/preferences/api" target="_blank" rel="noreferrer">
            https://www.steamgriddb.com/profile/preferences/api
          </Link>
        </Typography>
        <Box sx={{ mt: 3 }}>
          <Typography gutterBottom>
            {t("gameIconSize")}: <strong>{t(gameIconSizeLabelKey(gameIconSize))}</strong>
          </Typography>
          <Slider
            min={0}
            max={gameIconSizeMarks.length - 1}
            step={1}
            marks={gameIconSizeMarks.map((_, value) => ({
              value,
              label: gameIconSizeNamedMarks.find(mark => mark.value === value)
                ? t(gameIconSizeLabelKeys[value])
                : ""
            }))}
            value={gameIconSizeIndex(gameIconSize)}
            valueLabelDisplay="auto"
            valueLabelFormat={(value) => t(gameIconSizeLabelKeys[value])}
            sx={{
              color: "#0d6efd",
              height: 10,
              mt: 1,
              mb: 4,
              "& .MuiSlider-rail": {
                opacity: 0.25,
                borderRadius: 999
              },
              "& .MuiSlider-track": {
                border: "none",
                borderRadius: 999,
                background: "linear-gradient(90deg, #1f8fff 0%, #48c6ef 100%)"
              },
              "& .MuiSlider-thumb": {
                height: 24,
                width: 24,
                backgroundColor: "#fff",
                border: "3px solid #1f8fff",
                boxShadow: "0 0 0 6px rgba(31, 143, 255, 0.16)",
                "&:hover, &.Mui-focusVisible": {
                  boxShadow: "0 0 0 9px rgba(31, 143, 255, 0.22)"
                }
              },
              "& .MuiSlider-mark": {
                width: 4,
                height: 4,
                borderRadius: "50%",
                backgroundColor: "#fff",
                opacity: 0.9
              },
              "& .MuiSlider-markActive": {
                backgroundColor: "#fff"
              },
              "& .MuiSlider-valueLabel": {
                backgroundColor: "#1f8fff",
                fontWeight: 600
              },
              "& .MuiSlider-markLabel": {
                fontSize: "0.72rem",
                color: "text.secondary",
                top: 34
              }
            }}
            onChange={async (_, value) => {
              const nextIndex = Array.isArray(value) ? value[0] : value;
              const nextValue = gameIconSizeMarks[nextIndex];
              setGameIconSize(nextValue);
              await setGameIconSizeAction(`${nextValue}`);
            }}
          />
        </Box>
        <FormControl sx={{ mt: 3, mb: 1 }} fullWidth>
          <InputLabel id="locale-label">{t("locale")}</InputLabel>
          <Select labelId="locale-label" label={t("locale")} value={locale} onChange={(e) => setLocale(e.target.value)}>
            {LANGUAGES.map((l: string) => <MenuItem key={l} value={l.toLowerCase()}>{l.toUpperCase()}</MenuItem>)}
          </Select>
        </FormControl>
      </DialogContent>
      <DialogActions>
        <Button onClick={handleClose}>{t("cancel")}</Button>
        <Button onClick={handleSave} variant="contained">{t("save")}</Button>
      </DialogActions>
    </Dialog>
  </>;
};

export default SettingComponent;
