import "./setting.css";
import React, { useState } from "react";
import {
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

const gameIconSizeValue = (value: string) => {
  const legacySizes: { [key: string]: number } = {
    extraSmall: 90,
    small: 180,
    medium: 270,
    large: 360,
  };
  const numericValue = Number(value);
  return Number.isFinite(numericValue) ? numericValue : legacySizes[value] || 180;
};

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
        <Typography sx={{ mt: 3 }} gutterBottom>
          {t("gameIconSize")}: {gameIconSize}px
        </Typography>
        <Slider
          min={90}
          max={360}
          step={5}
          marks={[
            { value: 90, label: t("gameIconSizeExtraSmall") },
            { value: 180, label: t("gameIconSizeSmall") },
            { value: 270, label: t("gameIconSizeMedium") },
            { value: 360, label: t("gameIconSizeLarge") },
          ]}
          value={gameIconSize}
          valueLabelDisplay="auto"
          onChange={async (_, value) => {
            const nextValue = Array.isArray(value) ? value[0] : value;
            setGameIconSize(nextValue);
            await setGameIconSizeAction(`${nextValue}`);
          }}
        />
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
