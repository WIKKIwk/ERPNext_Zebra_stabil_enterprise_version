const byId = (id) => document.getElementById(id);

const healthDot = byId("health-dot");
const healthText = byId("health-text");
const configJson = byId("config-json");
const scaleWeight = byId("scale-weight");
const scaleUnit = byId("scale-unit");
const scaleMeta = byId("scale-meta");
const encodeResult = byId("encode-result");
const transceiveResult = byId("transceive-result");

const btnRefresh = byId("btn-refresh");
const btnResume = byId("btn-resume");
const btnReset = byId("btn-reset");

const encodeForm = byId("encode-form");
const transceiveForm = byId("transceive-form");

const encodeFields = {
  epc: byId("encode-epc"),
  copies: byId("encode-copies"),
  human: byId("encode-human"),
  feed: byId("encode-feed"),
  dryrun: byId("encode-dryrun"),
};

const transceiveFields = {
  zpl: byId("transceive-zpl"),
  timeout: byId("transceive-timeout"),
  max: byId("transceive-max"),
};

const formatJson = (obj) => JSON.stringify(obj, null, 2);

const fetchJson = async (url, options = {}) => {
  const response = await fetch(url, {
    headers: { "Content-Type": "application/json" },
    ...options,
  });
  const text = await response.text();
  let payload = null;
  if (text) {
    try {
      payload = JSON.parse(text);
    } catch {
      payload = { raw: text };
    }
  }
  if (!response.ok) {
    const message = payload?.message || payload?.error || response.statusText;
    throw new Error(message);
  }
  return payload;
};

const setResult = (node, message, isError = false) => {
  node.textContent = message || "";
  node.style.background = isError ? "rgba(190, 60, 40, 0.12)" : "rgba(47, 127, 115, 0.08)";
};

const refreshHealth = async () => {
  try {
    const payload = await fetchJson("/api/v1/health");
    healthDot.classList.add("ok");
    healthText.textContent = `${payload.service || "Service"} OK`;
  } catch (err) {
    healthDot.classList.remove("ok");
    healthText.textContent = `Error: ${err.message}`;
  }
};

const refreshConfig = async () => {
  try {
    const payload = await fetchJson("/api/v1/config");
    configJson.textContent = formatJson(payload);
  } catch (err) {
    configJson.textContent = `Error: ${err.message}`;
  }
};

const refreshScale = async () => {
  try {
    const payload = await fetchJson("/api/v1/scale");
    if (payload?.weight != null) {
      scaleWeight.textContent = payload.weight.toFixed(3);
      scaleUnit.textContent = payload.unit || "kg";
      scaleMeta.textContent = payload.stable == null ? "Unverified" : payload.stable ? "Stable" : "Unstable";
    } else {
      scaleWeight.textContent = "0.000";
      scaleUnit.textContent = payload?.unit || "kg";
      scaleMeta.textContent = payload?.error || "Waiting for data...";
    }
  } catch (err) {
    scaleMeta.textContent = `Error: ${err.message}`;
  }
};

const refreshAll = async () => {
  await Promise.all([refreshHealth(), refreshConfig(), refreshScale()]);
};

encodeForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  const body = {
    epc: encodeFields.epc.value.trim(),
    copies: Number(encodeFields.copies.value || 1),
    printHumanReadable: encodeFields.human.checked,
    feedAfterEncode: encodeFields.feed.checked,
    dryRun: encodeFields.dryrun.checked,
  };

  try {
    const payload = await fetchJson("/api/v1/encode", {
      method: "POST",
      body: JSON.stringify(body),
    });
    setResult(encodeResult, formatJson(payload));
  } catch (err) {
    setResult(encodeResult, `Error: ${err.message}`, true);
  }
});

transceiveForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  const body = {
    zpl: transceiveFields.zpl.value,
    readTimeoutMs: Number(transceiveFields.timeout.value || 2000),
    maxBytes: Number(transceiveFields.max.value || 32768),
  };

  try {
    const payload = await fetchJson("/api/v1/transceive", {
      method: "POST",
      body: JSON.stringify(body),
    });
    setResult(transceiveResult, formatJson(payload));
  } catch (err) {
    setResult(transceiveResult, `Error: ${err.message}`, true);
  }
});

btnRefresh.addEventListener("click", refreshAll);

btnResume.addEventListener("click", async () => {
  try {
    const payload = await fetchJson("/api/v1/printer/resume", { method: "POST" });
    setResult(encodeResult, formatJson(payload));
  } catch (err) {
    setResult(encodeResult, `Error: ${err.message}`, true);
  }
});

btnReset.addEventListener("click", async () => {
  try {
    const payload = await fetchJson("/api/v1/printer/reset", { method: "POST" });
    setResult(encodeResult, formatJson(payload));
  } catch (err) {
    setResult(encodeResult, `Error: ${err.message}`, true);
  }
});

refreshAll();
setInterval(refreshScale, 500);
