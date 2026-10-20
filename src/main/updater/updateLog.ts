import fs from "fs-extra";
import os from "os";
import path from "path";

const pad = (value: number) => (value < 10 ? `0${value}` : String(value));

export const formatLogTimestamp = (date = new Date()) =>
  `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;

// Same "[yyyy-MM-dd HH:mm:ss] message" lines the Windows update helper writes, so one update reads as one story.
export const appendUpdateLog = (logPath: string, message: string) => {
  try {
    fs.ensureDirSync(path.dirname(logPath));
    fs.appendFileSync(logPath, `[${formatLogTimestamp()}] ${message}${os.EOL}`, "utf8");
  } catch (error) {
    console.error(`Could not write to ${logPath}`, error);
  }
};
