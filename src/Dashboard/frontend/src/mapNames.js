// World map files spell some countries differently: the ECharts 4.9 map the app
// loads says "United States", Natural Earth (the world-atlas fallback) says
// "United States of America". Each group lists the names of one country, so a
// name from the model resolves to whichever spelling the loaded map contains.
const COUNTRY_GROUPS = [
  ["United States", "United States of America", "US", "USA", "U.S.", "U.S.A."],
  ["United Kingdom", "UK", "U.K.", "Great Britain", "Britain"],
  ["South Korea", "Korea", "Republic of Korea", "Korea, Republic of", "Korea, South"],
  ["North Korea", "Dem. Rep. Korea", "DPRK", "Democratic People's Republic of Korea", "Korea, North"],
  ["Czechia", "Czech Republic", "Czech Rep."],
  ["Dem. Rep. Congo", "DR Congo", "DRC", "Democratic Republic of the Congo", "Congo (DRC)", "Congo (Kinshasa)"],
  ["Congo", "Republic of the Congo", "Congo (Brazzaville)", "Rep. Congo"],
  ["Tanzania", "United Republic of Tanzania"],
  ["Côte d'Ivoire", "Ivory Coast", "Cote d'Ivoire"],
  ["Bosnia and Herzegovina", "Bosnia and Herz.", "Bosnia", "Bosnia & Herzegovina"],
  ["United Arab Emirates", "UAE"],
  ["Dominican Republic", "Dominican Rep."],
  ["Central African Republic", "Central African Rep."],
  ["Equatorial Guinea", "Eq. Guinea"],
  ["eSwatini", "Eswatini", "Swaziland"],
  ["Timor-Leste", "East Timor"],
  ["Myanmar", "Burma"],
  ["Lao PDR", "Laos"],
  ["Vatican City", "Vatican", "Holy See"],
  ["North Macedonia", "Macedonia", "FYROM"],
  ["Falkland Islands", "Falkland Is."],
  ["South Sudan", "S. Sudan"],
  ["Western Sahara", "W. Sahara"],
  ["Solomon Islands", "Solomon Is."],
  ["Russia", "Russian Federation"],
  ["Vietnam", "Viet Nam"],
  ["Iran", "Iran, Islamic Republic of", "Islamic Republic of Iran"],
  ["Syria", "Syrian Arab Republic"],
  ["Moldova", "Republic of Moldova"],
  ["Brunei", "Brunei Darussalam"],
  ["Turkey", "Türkiye", "Turkiye"],
  ["Palestine", "State of Palestine", "West Bank and Gaza"],
  ["Gambia", "The Gambia"],
  ["Bahamas", "The Bahamas"],
  ["Netherlands", "The Netherlands"],
  ["Slovakia", "Slovak Republic"],
  ["Taiwan", "Taiwan, Province of China"],
];

const key = (name) => String(name).normalize("NFKC").trim().toLowerCase();
const GROUP_OF = new Map(COUNTRY_GROUPS.flatMap((group) => group.map((name) => [key(name), group])));

/** The country names a loaded GeoJSON map contains. */
export function mapFeatureNames(geoJson) {
  return new Set(
    (geoJson?.features || [])
      .map((feature) => feature?.properties?.name)
      .filter((name) => typeof name === "string" && name),
  );
}

/** The spelling of `name` the loaded map uses; the name itself when the map has no match. */
export function resolveCountryName(name, known) {
  if (typeof name !== "string" || !known || known.size === 0 || known.has(name)) return name;
  for (const candidate of GROUP_OF.get(key(name)) || [])
    if (known.has(candidate)) return candidate;
  for (const candidate of known) if (key(candidate) === key(name)) return candidate;
  return name;
}
