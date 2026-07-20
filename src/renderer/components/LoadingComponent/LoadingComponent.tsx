import React from "react";
import { Button, Stack, Typography } from "@mui/material";
import "./loading.css";
import useTranslation from "../../i18n/I18nService";

type LoadingComponentProps = {
  error?: string;
  onRetry?: () => void;
};

const LoadingComponent = ({ error, onRetry }: LoadingComponentProps) => {
  const { t } = useTranslation();

  return (
    <div className="centred-container">
      <Stack spacing={2} alignItems="center">
        {
          error
            ? (
              <>
                <Typography variant="h6">{t("loading_error")}</Typography>
                <Typography variant="body2">{error}</Typography>
                <Button onClick={onRetry} variant="contained">{t("retry")}</Button>
              </>
            )
            : (
              <>
                <div className="ripple-container">
                  <span className="loading-ripple" />
                </div>
                <h3>{t("loading_data")}</h3>
              </>
            )
        }
      </Stack>
    </div>
  );
};

export default LoadingComponent;
