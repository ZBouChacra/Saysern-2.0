import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { renderBody } from "../pages/ContentPage";

describe("page bodies written by an admin", () => {
  it("become paragraphs, headings and lists", () => {
    const html = renderToStaticMarkup(<div>{renderBody("First paragraph.\n\n# A heading\nText under it.\n\n- one\n- two")}</div>);
    expect(html).toContain("<p>First paragraph.</p>");
    expect(html).toContain("<h2>A heading</h2>");
    expect(html).toContain("<p>Text under it.</p>");
    expect(html).toContain("<ul><li>one</li><li>two</li></ul>");
  });
  it("never turns what was typed into HTML", () => {
    const html = renderToStaticMarkup(<div>{renderBody('<script>alert(1)</script>\n\n- <b>bold?</b>')}</div>);
    expect(html).not.toContain("<script>");
    expect(html).not.toContain("<b>bold");
    expect(html).toContain("&lt;script&gt;");
  });
});
