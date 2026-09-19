const { defineConfig } = require("@playwright/test");

module.exports = defineConfig({
  testDir: ".",
  testMatch: "marker-bridge.spec.js",
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 10_000,
  use: { browserName: "chromium", headless: true }
});
