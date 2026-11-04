/*!
 * Cities: Skylines II UI Module
 *
 * Id: RealisticPolicies
 * Author: Boing787(kanghyeon01)
 * Version: 0.0.1
 * Dependencies:
 */
// Hand-written (no bundler): uses only the globals the game exposes to UI mods.
const React = window.React;
const api = window["cs2/api"];
const modding = window["cs2/modding"];
const l10n = window["cs2/l10n"];

const GROUP = "RealisticPolicies";
const POLICIES_PAGE_MODULE = "game-ui/game/components/city-info-panel/city-info-policies/policies-page.tsx";
const CITY_POLICY_MODULE = "game-ui/game/components/city-info-panel/city-info-policies/city-policy.tsx";
const BUDGET_SECTION_MODULE = "game-ui/game/components/economy-panel/budget-page/budget-section/budget-section.tsx";
const TEXT_INPUT_MODULE = "game-ui/common/input/text/text-input.tsx";
const SECTIONS_MODULE = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const CITY_INFO_FIELD_MODULE = "game-ui/game/components/toolbar/top/city-info-field/city-info-field.tsx";
const NUMBER_BADGE_MODULE = "game-ui/game/components/toolbar/components/number-badge/number-badge.tsx";
const WELFARE_SECTION_TYPE = "RealisticPolicies.WelfareSeriesSection";
const MANDATE_ID = "RealisticPolicies.EquipmentMandate";
const VANILLA_ID = "Advanced Pollution Management";

function getGameModule(path, name) {
    try {
        return modding.getModule(path, name);
    } catch (e) {
        return undefined;
    }
}

const TextInput = getGameModule(TEXT_INPUT_MODULE, "TextInput");
const InfoSection = getGameModule("game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx", "InfoSection");
const InfoRow = getGameModule("game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.tsx", "InfoRow");
const Checkbox = getGameModule("game-ui/common/input/toggle/checkbox/checkbox.tsx", "Checkbox");
const AnimatedNumberBadge = getGameModule(NUMBER_BADGE_MODULE, "AnimatedNumberBadge");
const budgetRows$ = api.bindValue(GROUP, "budgetRows", []);
const unansweredSuits$ = api.bindValue(GROUP, "unansweredSuits", 0);
const selfTestStage$ = api.bindValue(GROUP, "selfTestStage", 0);

// ---------------------------------------------------------------- unanswered suits badge

// The game's own number badge (the one on the milestone and map tile buttons) on the City Info button,
// counting suits the city has not answered. It ignores the mouse, so the button works as before.
function extendCityInfoField(Field) {
    return function RealisticPoliciesCityInfoField(props) {
        const count = api.useValue(unansweredSuits$);
        const ref = React.useRef(null);
        const [left, setLeft] = React.useState(null);
        React.useLayoutEffect(() => {
            // The button is the field's first element; put the badge on its top-right corner.
            const field = ref.current && ref.current.firstElementChild;
            const button = field && field.firstElementChild;
            if (field && button) {
                setLeft(button.getBoundingClientRect().right - ref.current.getBoundingClientRect().left);
            }
        });
        const field = React.createElement(Field, props);
        if (!count || !AnimatedNumberBadge) {
            return React.createElement("div", { ref, style: { display: "flex" } }, field);
        }
        return React.createElement("div", { ref, style: { display: "flex", position: "relative" } },
            field,
            left === null ? null : React.createElement("div", {
                style: { position: "absolute", top: 0, left: `${left}px`, width: 0, height: 0, pointerEvents: "none" },
            }, React.createElement(AnimatedNumberBadge, { value: count })));
    };
}

// ---------------------------------------------------------------- policy search

// One query shared by the search box and every policy row.
const search = {
    query: "",
    listeners: new Set(),
    set(value) {
        this.query = value;
        this.listeners.forEach((listener) => listener(value));
    },
};

function useSearchQuery() {
    const [query, setQuery] = React.useState(search.query);
    React.useEffect(() => {
        search.listeners.add(setQuery);
        return () => search.listeners.delete(setQuery);
    }, []);
    return query;
}

function matches(text, query) {
    return !!text && text.toLocaleLowerCase().indexOf(query) >= 0;
}

// Rows currently shown, for the self-test only.
const visibleRows = new Set();

function extendCityPolicy(Row) {
    return function RealisticPoliciesCityPolicy(props) {
        const query = useSearchQuery();
        const { translate } = l10n.useLocalization();
        const policy = props.policy;
        const id = policy && policy.id;
        const needle = query.trim().toLocaleLowerCase();
        const shown = !needle || matches(translate(`Policy.TITLE[${id}]`, id), needle) || matches(id, needle);
        if (shown) {
            visibleRows.add(id);
        } else {
            visibleRows.delete(id);
        }
        return shown ? React.createElement(Row, props) : null;
    };
}

function SearchBox() {
    const query = useSearchQuery();
    const { translate } = l10n.useLocalization();
    const placeholder = translate("RealisticPolicies.Search.Placeholder", "Search policies");
    const onChange = (e) => search.set((e && e.target ? e.target.value : e) || "");
    const props = { className: "rp-policy-search", value: query, placeholder, onChange };
    return React.createElement(
        "div",
        { style: { padding: "8rem 10rem 0 10rem" } },
        TextInput ? React.createElement(TextInput, props) : React.createElement("input", Object.assign({ type: "text" }, props)),
    );
}

function extendPoliciesPage(Page) {
    return function RealisticPoliciesPage(props) {
        // Clear the filter when the page closes so the next visit starts with the full list.
        React.useEffect(() => () => search.set(""), []);
        return React.createElement(
            "div",
            { style: { display: "flex", flexDirection: "column", flex: "1 1 auto", minHeight: 0 } },
            React.createElement(SearchBox),
            React.createElement(Page, props),
        );
    };
}

// ---------------------------------------------------------------- budget rows

// Budget rows appended by the last render, for the self-test only.
let lastInjectedRows = 0;

// The mod's money is already inside a vanilla source (so every vanilla total is right);
// here that share is moved out of the host row into its own row.
function extendBudgetSection(Section) {
    return function RealisticPoliciesBudgetSection(props) {
        const rows = api.useValue(budgetRows$);
        if (!rows || rows.length === 0 || !props.items || !props.values) {
            return React.createElement(Section, props);
        }
        let items = null;
        let values = null;
        for (const row of rows) {
            for (const item of props.items) {
                const source = item.sources && item.sources.find((s) => s.id === row.host);
                if (!source) {
                    continue;
                }
                items = items || props.items.slice();
                values = values || props.values.slice();
                values[source.index] -= row.value;
                values.push(row.value);
                items.push({ id: row.id, color: item.color, icon: item.icon, active: true, sources: [{ id: row.id, index: values.length - 1 }] });
                break;
            }
        }
        if (!items) {
            return React.createElement(Section, props);
        }
        lastInjectedRows += items.length - props.items.length;
        return React.createElement(Section, Object.assign({}, props, { items, values }));
    };
}

// ---------------------------------------------------------------- welfare office series

// Welfare office info panel: which civil-service series its staff count as (one choice, drawn as checkboxes).
const SERIES_CHOICES = ["GeneralAdministration", "SocialWelfare", "Tax"];

function WelfareSeriesSection(props) {
    const { translate } = l10n.useLocalization();
    const rows = SERIES_CHOICES.map((name, index) => React.createElement(InfoRow, {
        key: name,
        left: translate(`RealisticPolicies.Series.${name}`, name),
        right: React.createElement(Checkbox, { checked: props.series === index, onChange: () => api.trigger(GROUP, "SetWelfareSeries", index) }),
        tooltip: translate(`RealisticPolicies.Series.${name}.Tooltip`, ""),
        subRow: true,
    }));
    return React.createElement(InfoSection, { focusKey: props.focusKey, disableFocus: true },
        React.createElement(InfoRow, { left: translate("RealisticPolicies.Series.Header", "Staff series"), uppercase: true }),
        ...rows);
}

// ---------------------------------------------------------------- prototype self-test

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function report(message) {
    api.trigger(GROUP, "uiReport", message);
}

function check(name, ok, detail) {
    report(`check:${ok ? 1 : 0}:${name}${detail !== undefined ? "|" + detail : ""}`);
}

async function runSelfTest(translate) {
    try {
        // The policy list itself is checked through the rendered rows below (a second binding subscription stays empty).
        const title = translate(`Policy.TITLE[${MANDATE_ID}]`, "");
        check("Mod policy title is localized", !!title && title !== MANDATE_ID, title);
        check("Game text input component found", !!TextInput);

        api.trigger("game", "showCityInfoPanel", 1);
        await sleep(3000);
        check("Search box is rendered on the policy page", !!document.querySelector(".rp-policy-search"));
        const allRows = visibleRows.size;
        report(`Rows rendered without a filter: ${allRows}`);
        report("shot:policies-all");
        await sleep(1500);

        const needle = title.substring(0, Math.min(4, title.length));
        search.set(needle);
        await sleep(1500);
        const filtered = Array.from(visibleRows);
        check("Search filter hides non-matching rows", filtered.length > 0 && filtered.length < allRows && filtered.indexOf(MANDATE_ID) >= 0, `"${needle}" -> ${filtered.join(", ")}`);
        check("Vanilla toggle is not rendered", !visibleRows.has(VANILLA_ID) && allRows > 0);
        report("shot:policies-search");
        await sleep(1500);
        search.set("");

        api.trigger("game", "closeActiveGamePanel");
        await sleep(1000);
        lastInjectedRows = 0;
        api.trigger("game", "showEconomyPanel", 0);
        await sleep(3000);
        check("Budget page renders the mod rows", lastInjectedRows >= 2, `${lastInjectedRows} rows appended`);
        const text = document.body ? document.body.textContent || "" : "";
        const taxName = translate("EconomyPanel.BUDGET_ITEM[RealisticPolicies.PollutionTax]", "");
        check("Budget page shows the mod row names", !!taxName && text.indexOf(taxName) >= 0, taxName);
        report("shot:budget");
        await sleep(1500);
        api.trigger("game", "closeActiveGamePanel");
    } catch (e) {
        check("UI self-test ran without errors", false, String(e && e.stack ? e.stack : e));
    }
    report("done");
}

async function runWelfareTest(translate) {
    try {
        await sleep(2500);
        const label = translate("RealisticPolicies.Series.Tax", "");
        const text = document.body ? document.body.textContent || "" : "";
        check("Welfare office panel shows the series choices", !!label && text.indexOf(label) >= 0, label);
        report("shot:welfare-before");
        await sleep(1500);
        api.trigger(GROUP, "SetWelfareSeries", 1);
        await sleep(2000);
        report("shot:welfare-after");
        await sleep(1500);
    } catch (e) {
        check("Welfare UI test ran without errors", false, String(e && e.stack ? e.stack : e));
    }
    report("done");
}

function SelfTestRunner() {
    const stage = api.useValue(selfTestStage$);
    const { translate } = l10n.useLocalization();
    React.useEffect(() => {
        if (stage === 1) {
            runSelfTest(translate);
        } else if (stage === 2) {
            runWelfareTest(translate);
        }
    }, [stage]);
    return null;
}

export default function register(moduleRegistry) {
    moduleRegistry.extend(POLICIES_PAGE_MODULE, "CityInfoPolicies", extendPoliciesPage);
    moduleRegistry.extend(CITY_POLICY_MODULE, "CityPolicy", extendCityPolicy);
    moduleRegistry.extend(BUDGET_SECTION_MODULE, "BudgetSection", extendBudgetSection);
    moduleRegistry.extend(CITY_INFO_FIELD_MODULE, "CityInfoField", extendCityInfoField);
    moduleRegistry.append("Game", SelfTestRunner);
    if (InfoSection && InfoRow && Checkbox) {
        moduleRegistry.extend(SECTIONS_MODULE, "selectedInfoSectionComponents", (components) => {
            components[WELFARE_SECTION_TYPE] = WelfareSeriesSection;
            return components;
        });
    } else {
        console.error("RealisticPolicies: info panel components not found; the welfare office option is unavailable.");
    }
}

export const hasCSS = false;
