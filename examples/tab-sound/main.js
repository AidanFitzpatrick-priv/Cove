cove.on("tab-created", () => {
  const audio = new Audio(cove.file("pop.wav"));
  const volume = Number(cove.setting("volume"));
  audio.volume = Number.isFinite(volume) ? Math.min(1, Math.max(0, volume)) : 0.4;
  audio.play().catch(() => {});
});
