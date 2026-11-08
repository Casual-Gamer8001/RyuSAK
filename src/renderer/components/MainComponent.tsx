import React, { useEffect } from "react";
import useStore from "../actions/state";
import GameListingComponent from "./GameListingComponent/GameListingComponent";
import SettingComponent from "./SettingComponent/SettingComponent";
import {
  Box,
  Container,
  FormControl,
  Grid,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  SelectChangeEvent,
  Stack,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip
} from "@mui/material";
import DeleteOutlineOutlinedIcon from "@mui/icons-material/DeleteOutlineOutlined";
import EditOutlinedIcon from "@mui/icons-material/EditOutlined";
import FolderOpenOutlinedIcon from "@mui/icons-material/FolderOpenOutlined";
import useTranslation from "../i18n/I18nService";
import discord_logo from "../resources/discord_logo.png";
import { EmulatorConfigMeta, EmulatorKind } from "../../types";
import { EMULATOR_KINDS, EMULATOR_LABELS, kindOf } from "../../emulators";
import { selectedConfigKey } from "../actions/emulatorConfig.action";

const ADD_CONFIG = "add_config";

const MainComponent = () => {
  const [
    emulatorConfigs,
    currentEmulator,
    switchEmulatorAction,
    removeEmulatorConfigAction,
    renameEmulatorConfigAction,
    changeEmulatorConfigPathAction,
    selectedConfig,
    setSelectConfigAction,
    addNewEmulatorConfigAction,
    createDefaultConfig,
    renameLegacyDefaultConfigsAction,
  ] = useStore(state => [
    state.emulatorConfigs,
    state.currentEmulator,
    state.switchEmulatorAction,
    state.removeEmulatorConfigAction,
    state.renameEmulatorConfigAction,
    state.changeEmulatorConfigPathAction,
    state.selectedConfig,
    state.setSelectConfigAction,
    state.addNewEmulatorConfigAction,
    state.createDefaultConfig,
    state.renameLegacyDefaultConfigsAction,
  ]);

  const { t } = useTranslation();
  const configsForEmulator: EmulatorConfigMeta[] = emulatorConfigs.filter((config: EmulatorConfigMeta) => kindOf(config) === currentEmulator);

  const onConfigurationChange = (e: SelectChangeEvent) => {
    const { value } = e.target;

    if (value === ADD_CONFIG) {
      addNewEmulatorConfigAction();
    } else {
      setSelectConfigAction(configsForEmulator.find(i => i.path === value));
    }

    e.preventDefault();
    return false;
  };

  const onEmulatorChange = (_: React.MouseEvent<HTMLElement>, value: EmulatorKind | null) => {
    if (value) {
      switchEmulatorAction(value);
    }
  };

  useEffect(() => {
    renameLegacyDefaultConfigsAction();
  }, []);

  useEffect(() => {
    if (configsForEmulator.length === 0) {
      createDefaultConfig();
    } else {
      const selectedPath = localStorage.getItem(selectedConfigKey(currentEmulator));
      const config = configsForEmulator.find(f => f.path === selectedPath) || configsForEmulator[0];
      setSelectConfigAction(config);
    }
  }, [emulatorConfigs, currentEmulator]);

  const isSelectedConfigCurrent = selectedConfig && kindOf(selectedConfig) === currentEmulator;

  return (
    <Container maxWidth={false}>
      <br />
      <Stack spacing={2}>
        <Box>
          <ToggleButtonGroup
            exclusive
            size="small"
            color="primary"
            value={currentEmulator}
            onChange={onEmulatorChange}
            aria-label={t("emulator")}
          >
            {
              EMULATOR_KINDS.map(kind => (
                <ToggleButton key={kind} value={kind} sx={{ px: 3 }}>{EMULATOR_LABELS[kind]}</ToggleButton>
              ))
            }
          </ToggleButtonGroup>
        </Box>
        {
          isSelectedConfigCurrent && (<>
            <Grid container spacing={2}>
              <Grid item xs={10} lg={11}>
                <Grid container spacing={0.5}>
                  <Grid item xs={9} lg={7}>
                    <Tooltip placement="right" title={`${t("readingDataPath")} ${selectedConfig.path}`}>
                      <FormControl fullWidth>
                        <InputLabel id="emulator-select-path-label">{t("configuration")}</InputLabel>
                        <Select
                          labelId="emulator-select-path-label"
                          id="emulator-select-path"
                          value={selectedConfig?.path || ""}
                          onChange={onConfigurationChange}
                        >
                          <MenuItem value={ADD_CONFIG}><i>{t("addConfiguration")}</i></MenuItem>
                          {
                            configsForEmulator.map((item, index) => (
                              <MenuItem key={`emulator-select-path-item-${index}`} value={item.path}>{item.name}</MenuItem>
                            ))
                          }
                        </Select>
                      </FormControl>
                    </Tooltip>
                  </Grid>
                  <Grid item xs={3} lg={3}>
                    <Box sx={{ alignItems: "center", display: "flex", gap: 0.25, height: "52px" }}>
                      <Tooltip title={t("renameConfiguration")}>
                        <IconButton onClick={() => renameEmulatorConfigAction(selectedConfig.path)} color="primary" size="small">
                          <EditOutlinedIcon />
                        </IconButton>
                      </Tooltip>
                      <Tooltip title={t("changeConfigurationPath")}>
                        <IconButton onClick={() => changeEmulatorConfigPathAction(selectedConfig.path)} color="primary" size="small">
                          <FolderOpenOutlinedIcon />
                        </IconButton>
                      </Tooltip>
                      {
                        (!selectedConfig.isDefault) && (
                          <Tooltip title={t("deleteConfiguration")}>
                            <IconButton onClick={() => removeEmulatorConfigAction(selectedConfig.path)} color="error" size="small">
                              <DeleteOutlineOutlinedIcon />
                            </IconButton>
                          </Tooltip>
                        )
                      }
                    </Box>
                  </Grid>
                </Grid>
              </Grid>
              <Grid item style={{ lineHeight: "52px" }} xs={2} lg={1}>
                <Grid container spacing={2}>
                  <Grid item xs={4}>
                    <a href="https://discord.gg/kKKFmnaZyc" className="no-blank-icon" target="_blank">
                      <IconButton edge="start" color="inherit">
                        <img height={40} src={discord_logo} />
                      </IconButton>
                    </a>
                  </Grid>
                  <Grid item xs={1}>
                    <SettingComponent />
                  </Grid>
                </Grid>
              </Grid>
            </Grid>
            <GameListingComponent config={selectedConfig} />
          </>)
        }
      </Stack>
    </Container>
  );
};

export default MainComponent;
