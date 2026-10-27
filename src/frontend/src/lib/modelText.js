// Hosted web search makes the model write private-use citation tokens such as
// U+E200 "cite" U+E202 "turn0search0" U+E201 into its text. They are not content
// and render as garbage. A streaming buffer can also end inside one.
const CITATION = /[ \t]*\uE200cite\uE202[^\uE200\uE201]{0,500}\uE201/g;
const PENDING = /[ \t]*\uE200[^\uE200\uE201]{0,500}$/;

export function stripCitationMarkers(text) {
  if (typeof text !== "string" || !text.includes("\uE200")) return text;
  return text.replace(CITATION, "").replace(PENDING, "");
}
