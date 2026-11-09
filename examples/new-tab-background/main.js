function paint() {
  if (!location.pathname.includes("newtab")) return;
  const image = cove.setting("image");
  for (const target of [document.documentElement, document.body]) {
    if (!target) continue;
    target.style.backgroundColor = "#121316";
    target.style.backgroundImage = image ? `url("${image}")` : "";
    target.style.backgroundSize = "cover";
    target.style.backgroundPosition = "center";
    target.style.backgroundRepeat = "no-repeat";
  }
}
paint();
cove.on("settings", (detail) => { if (!detail.id || detail.id === cove.id) paint(); });
