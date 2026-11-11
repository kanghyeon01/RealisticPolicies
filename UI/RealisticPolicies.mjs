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
const PROTEST_SECTION_TYPE = "RealisticPolicies.ProtestInfoSection";
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
// The glossary panel's search field (rounded, with a clear button), in the glossary theme (2026-11-04 user choice).
const SearchField = getGameModule("game-ui/editor/widgets/search-field/search-field.tsx", "SearchField");
const glossarySearchTheme = getGameModule("game-ui/editor/widgets/search-field/themes/search-field-glossary.module.scss", "classes");
const AutoNavigationScope = getGameModule("game-ui/common/focus/auto-navigation-scope.tsx", "AutoNavigationScope");
const NavigationDirection = getGameModule("game-ui/common/focus/navigation.ts", "NavigationDirection");
const InfoSection = getGameModule("game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx", "InfoSection");
const InfoRow = getGameModule("game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.tsx", "InfoRow");
const AnimatedNumberBadge = getGameModule(NUMBER_BADGE_MODULE, "AnimatedNumberBadge");
const Dropdown = getGameModule("game-ui/common/input/dropdown/dropdown.tsx", "Dropdown");
const DropdownToggle = getGameModule("game-ui/common/input/dropdown/dropdown-toggle.tsx", "DropdownToggle");
const DropdownItem = getGameModule("game-ui/common/input/dropdown/items/dropdown-item.tsx", "DropdownItem");
const gameDropdownTheme = getGameModule("game-ui/game/themes/game-dropdown.module.scss", "classes");
const budgetRows$ = api.bindValue(GROUP, "budgetRows", []);
const unansweredSuits$ = api.bindValue(GROUP, "unansweredSuits", 0);
const selfTestStage$ = api.bindValue(GROUP, "selfTestStage", 0);

// ---------------------------------------------------------------- unanswered suits badge

// The game's own number badge (the one on the milestone and map tile buttons) on the City Info button,
// counting suits the city has not answered. It ignores the mouse, so the button works as before.
function extendCityInfoField(Field) {
    return function RealisticPoliciesCityInfoField(props) {
        const count = api.useValue(unansweredSuits$) + useWaitingProtests();
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

// Policy the self-test wants selected on the policy page, and whether suits open their claims.
let testSelectPolicy = null;
let testOpenClaims = false;

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
        React.useEffect(() => {
            if (testSelectPolicy && testSelectPolicy === id && props.onSelect) {
                props.onSelect(policy);
            }
        });
        return shown ? React.createElement(Row, sliderFloor(props)) : null;
    };
}

// The game's policy row draws its slider only while the slider value is not 0 (`value && slider`): a mod slider pushed to
// 0% vanished and left a bare "0" by the checkbox, on or off (2026-11-10 user report). Its own sliders never reach 0.
// So the mod's sliders stop one step above 0 on screen; switching the policy off stays with the checkbox. The engine's
// range is not changed.
function sliderFloor(props) {
    const policy = props.policy;
    const data = policy && policy.data;
    if (!data || !data.range || data.range.min !== 0 || !(policy.id || "").startsWith("RealisticPolicies.")) {
        return props;
    }
    const floor = data.step > 0 ? data.step : 1;
    const range = Object.assign({}, data.range, { min: floor });
    const value = Math.max(data.value || 0, floor);
    return Object.assign({}, props, { policy: Object.assign({}, policy, { data: Object.assign({}, data, { range, value }) }) });
}

function SearchBox() {
    const query = useSearchQuery();
    const { translate } = l10n.useLocalization();
    const placeholder = translate("RealisticPolicies.Search.Placeholder", "Search policies");
    const onChange = (e) => search.set((e && e.target ? e.target.value : e) || "");
    let field;
    if (SearchField && glossarySearchTheme) {
        field = React.createElement(SearchField, { value: query, placeholder, onChange, theme: glossarySearchTheme });
    } else {
        const props = { value: query, placeholder, onChange };
        field = TextInput ? React.createElement(TextInput, props) : React.createElement("input", Object.assign({ type: "text" }, props));
    }
    return React.createElement("div", { className: "rp-policy-search", style: { padding: "8rem 6rem 6rem 6rem" } }, field);
}

function extendPoliciesPage(Page) {
    return function RealisticPoliciesPage(props) {
        // Clear the filter when the page closes so the next visit starts with the full list.
        React.useEffect(() => () => search.set(""), []);
        // The policy detail column below reads this to add the policy report (stage 7).
        // The game's page fills the whole tab height; with the search box above it, it ran one box-height below the panel
        // (2026-11-10 user report). It gets only the height left under the search box.
        const column = React.createElement(
            "div",
            { style: { display: "flex", flexDirection: "column", flex: "1 1 auto", minHeight: 0, height: "100%" } },
            React.createElement(SearchBox),
            React.createElement("div", { className: "rp-policy-list", style: { display: "flex", flexDirection: "column", flex: "1 1 0", minHeight: 0, overflow: "hidden" } },
                React.createElement(Page, props)),
        );
        // The tab content hosts a single focus child: the search box and the page share one navigation scope.
        const scoped = AutoNavigationScope && NavigationDirection
            ? React.createElement(AutoNavigationScope, { direction: NavigationDirection.Vertical }, column)
            : column;
        return React.createElement(PolicyPageContext.Provider, { value: true }, scoped);
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

// Welfare office info panel: which civil-service series its staff count as, picked from the game's dropdown
// (the one the route panel uses for vehicles).
const SERIES_CHOICES = ["GeneralAdministration", "SocialWelfare", "Tax"];

function WelfareSeriesSection(props) {
    const { translate } = l10n.useLocalization();
    const label = (name) => translate(`RealisticPolicies.Series.${name}`, name);
    const tooltip = (name) => translate(`RealisticPolicies.Series.${name}.Tooltip`, "");
    // The game's dropdown item takes a localized string (plain text showed "<INVALID TYPE>", 2026-11-10 user report).
    const localized = (name) => ({ __Type: "Game.UI.Localization.LocalizedString", id: `RealisticPolicies.Series.${name}.Tooltip`, value: null, args: null });
    const current = SERIES_CHOICES[props.series] || SERIES_CHOICES[0];
    const items = SERIES_CHOICES.map((name, index) => React.createElement(DropdownItem, {
        key: name,
        value: index,
        selected: props.series === index,
        tooltip: localized(name),
        onChange: (value) => api.trigger(GROUP, "SetWelfareSeries", value),
    }, label(name)));
    const dropdown = React.createElement(Dropdown, { theme: gameDropdownTheme, content: items },
        React.createElement(DropdownToggle, null, label(current)));
    return React.createElement(InfoSection, { focusKey: props.focusKey, disableFocus: true },
        React.createElement(InfoRow, {
            left: translate("RealisticPolicies.Series.Header", "Staff series"),
            right: dropdown,
            tooltip: tooltip(current),
            uppercase: true,
        }));
}

// ---------------------------------------------------------------- report panel (stage 7)

// Game parts the report is built from: the City Info panel's tab bar and content, the policy detail column, info
// rows, buttons, slider, number input, scrolling and entity names.
const CITY_INFO_PANEL_MODULE = "game-ui/game/components/city-info-panel/city-info-panel.tsx";
const TABS_MODULE = "game-ui/common/tabs/tabs.tsx";
const DETAIL_LAYOUT_MODULE = "game-ui/common/section/detail-layout/detail-layout.tsx";
const Tab = getGameModule(TABS_MODULE, "Tab");
const Slider = getGameModule("game-ui/common/input/slider/slider.tsx", "Slider");
const useStepTransformer = getGameModule("game-ui/common/input/slider/slider.tsx", "useStepTransformer");
const IntInput = getGameModule("game-ui/common/input/text/int-input.tsx", "IntInput");
const Scrollable = getGameModule("game-ui/common/scrolling/scrollable.tsx", "Scrollable");
const Checkbox = getGameModule("game-ui/common/input/toggle/checkbox/checkbox.tsx", "Checkbox");
const useNameFormat = getGameModule("game-ui/common/localization/localized-entity-name.tsx", "useNameFormat");
const ui = window["cs2/ui"];

const LAWSUITS_TAB = 2;
const PROTESTS_TAB = 3;
const policyReports$ = api.bindValue(GROUP, "policyReports", []);
const lawsuits$ = api.bindValue(GROUP, "lawsuits", null);
const protests$ = api.bindValue(GROUP, "protests", null);

// Protests the city has not answered yet (they count on the City Info badge with the suits).
function useWaitingProtests() {
    const data = api.useValue(protests$);
    return data && data.list ? data.list.filter((p) => p.response === 0 && !p.granted).length : 0;
}

// Set by the City Info panel wrapper for its tab bar and tab content; set by the policy page for its detail column.
const CityInfoContext = React.createContext(null);
const PolicyPageContext = React.createContext(false);

function useText() {
    const { translate } = l10n.useLocalization();
    return React.useCallback((key, args) => {
        let text = translate(`RealisticPolicies.UI.${key}`, key) || key;
        if (args) {
            for (const name in args) {
                text = text.split(`{${name}}`).join(args[name]);
            }
        }
        return text;
    }, [translate]);
}

function groupDigits(value) {
    const sign = value < 0 ? "-" : "";
    const digits = String(Math.round(Math.abs(value)));
    return sign + digits.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
}

// Money as the game writes it (Korean "¢12,130", monthly "¢12,130/월").
function useMoney() {
    const { translate } = l10n.useLocalization();
    return React.useCallback((value, monthly) => {
        const pattern = translate(monthly ? "Common.VALUE_MONEY_PER_MONTH" : "Common.VALUE_MONEY", "{SIGN}¢{VALUE}") || "{SIGN}¢{VALUE}";
        return pattern.split("{SIGN}").join(value < 0 ? "-" : "").split("{VALUE}").join(groupDigits(Math.abs(value)));
    }, [translate]);
}

const percent = (value) => `${Math.round(value * 100)}%`;
const signedPercent = (value) => `${value > 0 ? "+" : ""}${Math.round(value * 100)}%`;
const points = (value) => `${value > 0 ? "+" : ""}${Math.round(value * 100)}%p`;

// Korean particles (이/가, 은/는, 을/를) follow the final consonant of the word as read in Korean (docs/PLAN.md §7 "조사"):
// Hangul by its last syllable, numbers as read aloud, romanized Korean by the Revised Romanization finals (k t p n m ng l),
// English by the loanword rules (final nasals and l stay; p t k only after a short vowel; others gain 으).
// Returns null only where the spelling cannot tell (-oot: foot 풋, boot 부트); both forms are written then.
function finalConsonant(text) {
    const word = String(text).trim().split(/\s+/).pop() || "";
    const digits = word.match(/[0-9]+$/);
    if (digits) {
        const trimmed = digits[0].replace(/0+$/, "");
        return trimmed.length < digits[0].length || "13678".includes(trimmed.slice(-1));
    }
    const c = word.charCodeAt(word.length - 1);
    if (c >= 0xac00 && c <= 0xd7a3) {
        return (c - 0xac00) % 28 !== 0;
    }
    if (!/[a-z]$/i.test(word)) {
        return null;
    }
    const w = word.toLowerCase();
    if (/(m|n|ng|l)$/.test(w) || /[lmn]e$/.test(w)) {
        return true;
    }
    if (/[ptk]$/.test(w)) {
        if (/(eo|eu|ae|oe|wi|ui)[ptk]$/.test(w) || /ck$/.test(w) || /(^|[^aeiouy])[aeiouy][ptk]$/.test(w) || /ook$/.test(w)) {
            return true;
        }
        return /oot$/.test(w) ? null : false;
    }
    return false;
}

function josa(word, withFinal, withoutFinal) {
    const f = finalConsonant(word);
    return word + (f === null ? `${withFinal}(${withoutFinal})` : f ? withFinal : withoutFinal);
}

const GOVERNMENTS = ["City", "County", "State"];

// Defendants: the office that made the disposition and the body that pays (docs/PLAN.md §7 "자치단체 종류").
function defendants(text, korean, cityName, government) {
    const kind = GOVERNMENTS[government] || "City";
    let name = (cityName || "").trim() || "City";
    if (korean) {
        const suffix = kind === "City" ? "시" : kind === "County" ? "군" : "";
        if (suffix && name.endsWith(suffix) && name.length > suffix.length) {
            name = name.slice(0, -suffix.length).trimEnd();
        }
        const joined = /[가-힣]$/.test(name) ? name : `${name} `;
        return {
            holder: text(`Defendant.${kind}.Holder`, { NAME: joined }).trim(),
            body: text(`Defendant.${kind}.Body`, { NAME: kind === "State" ? name : joined }).trim(),
        };
    }
    if (kind === "State" && /state$/i.test(name)) {
        return { holder: text("Defendant.State.Holder", { NAME: name }), body: name };
    }
    return { holder: text(`Defendant.${kind}.Holder`, { NAME: name }), body: text(`Defendant.${kind}.Body`, { NAME: name }) };
}

// A tooltip on the whole row sat in its middle, the middle of the screen in the wide protests and lawsuits tabs
// (2026-11-10 user report): it goes on the row's name instead, as close to the pointer as the text.
function Row(props) {
    if (props.tooltip && ui && ui.Tooltip) {
        const left = React.createElement(ui.Tooltip, { tooltip: props.tooltip, direction: "up", alignment: "start" },
            React.createElement("span", null, props.left));
        return React.createElement(InfoRow, Object.assign({ disableFocus: true }, props, { left, tooltip: undefined }));
    }
    return React.createElement(InfoRow, Object.assign({ disableFocus: true }, props));
}

// ---- City Info panel: a third tab, Lawsuits

// Opening the panel while suits wait for an answer goes straight to the Lawsuits tab (the badge leads there).
function extendCityInfoPanel(Panel) {
    return function RealisticPoliciesCityInfoPanel(props) {
        const unanswered = api.useValue(unansweredSuits$);
        const waiting = useWaitingProtests();
        React.useEffect(() => {
            if (unanswered > 0 && props.selectedTab !== LAWSUITS_TAB) {
                api.trigger("game", "showCityInfoPanel", LAWSUITS_TAB);
            } else if (unanswered === 0 && waiting > 0 && props.selectedTab !== PROTESTS_TAB) {
                api.trigger("game", "showCityInfoPanel", PROTESTS_TAB);
            }
        }, []);
        const value = React.useMemo(() => ({ selectedTab: props.selectedTab, onSelectTab: props.onSelectTab }), [props.selectedTab, props.onSelectTab]);
        return React.createElement(CityInfoContext.Provider, { value }, React.createElement(Panel, props));
    };
}

function extendTabBar(TabBar) {
    return function RealisticPoliciesTabBar(props) {
        const panel = React.useContext(CityInfoContext);
        const text = useText();
        if (!panel || !Tab) {
            return React.createElement(TabBar, props);
        }
        const children = React.Children.toArray(props.children);
        children.push(React.createElement(Tab, {
            key: "rp-lawsuits",
            id: LAWSUITS_TAB,
            selectedId: panel.selectedTab,
            onSelect: panel.onSelectTab,
        }, text("Tab.Lawsuits")));
        children.push(React.createElement(Tab, {
            key: "rp-protests",
            id: PROTESTS_TAB,
            selectedId: panel.selectedTab,
            onSelect: panel.onSelectTab,
        }, text("Tab.Protests")));
        // The tab content below must not see this context as its own tab bar.
        return React.createElement(CityInfoContext.Provider, { value: null }, React.createElement(TabBar, props, ...children));
    };
}

function extendTabNav(TabNav) {
    return function RealisticPoliciesTabNav(props) {
        const panel = React.useContext(CityInfoContext);
        if (!panel) {
            return React.createElement(TabNav, props);
        }
        const tabs = (props.tabs || []).concat([LAWSUITS_TAB, PROTESTS_TAB]);
        const children = props.selectedTab === LAWSUITS_TAB ? React.createElement(LawsuitsPage)
            : props.selectedTab === PROTESTS_TAB ? React.createElement(ProtestsPage) : props.children;
        return React.createElement(CityInfoContext.Provider, { value: null }, React.createElement(TabNav, Object.assign({}, props, { tabs }), children));
    };
}

// ---- Policy report under the policy description

function extendDetailLayout(Layout) {
    return function RealisticPoliciesDetailLayout(props) {
        const onPolicyPage = React.useContext(PolicyPageContext);
        const id = onPolicyPage && props.title && props.title.props ? props.title.props.hash : null;
        if (!id || typeof id !== "string" || id.indexOf(`${GROUP}.`) !== 0) {
            return React.createElement(Layout, props);
        }
        return React.createElement(Layout, props, props.children, React.createElement(PolicyReport, { key: "rp-report", id }));
    };
}

// Policies the mod reports on at the last render of a report, for the self-test only.
let lastReportCount = 0;

function PolicyReport({ id }) {
    const reports = api.useValue(policyReports$) || [];
    lastReportCount = reports.length;
    const suits = api.useValue(lawsuits$);
    const text = useText();
    const money = useMoney();
    const { translate } = l10n.useLocalization();
    const report = reports.find((r) => r.id === id);
    if (!report) {
        return null;
    }
    const sections = [];
    const caseNo = (year, number) => text("Suit.CaseNumber", { YEAR: year, N: number });

    const ban = suits && suits.bans ? suits.bans.find((b) => b.policyId === id) : null;
    // Detail setting (second slider), also while the policy is off so it can be set first.
    const second = report.second ? React.createElement(SecondSection, { key: "second", id, second: report.second, locked: !!ban }) : null;
    if (ban) {
        sections.push(React.createElement(InfoSection, { key: "ban", disableFocus: true },
            Row({ left: text("Report.Banned"), right: text("Report.DaysLeft", { DAYS: ban.daysLeft.toFixed(1) }) }),
            ban.number > 0 ? Row({ left: text("Report.Verdict"), right: text(ban.partial ? "Suit.LostPart" : "Suit.Lost", { CASE: caseNo(ban.year, ban.number) }) }) : null));
        // The detail setting as the ruling left it, locked until the ban ends (2026-11-12 user decision, option 2).
        if (second) {
            sections.push(second);
        }
        return React.createElement("div", { className: "rp-policy-report" }, sections);
    }
    if (!report.on) {
        const off = report.mechanism === "AllowanceAuction" ? "Report.NeedsTrading" : "Report.Off";
        return React.createElement("div", { className: "rp-policy-report" },
            second,
            React.createElement(InfoSection, { key: "off", disableFocus: true }, Row({ left: text(off) })));
    }
    if (second) {
        sections.push(second);
    }

    const seriesName = (series) => translate(`RealisticPolicies.SeriesName.${series}`, series);
    const input = report.staff.map((s) => Row({
        key: `staff-${s.series}`, left: seriesName(s.series), subRow: true, tooltip: text("Report.Staff.Tooltip"),
        right: text("Report.Staff", { NEED: Math.ceil(s.need), WORKERS: s.workers }),
    })).concat(report.money.filter((m) => !m.income).map((m) => Row({
        key: `money-${m.row}`, left: translate(`EconomyPanel.BUDGET_ITEM[${m.row}]`, m.row), subRow: true, right: money(m.monthly, true),
    })));

    const activity = [
        Row({ key: "eff", left: text("Report.Effectiveness"), right: percent(report.effectiveness), subRow: true, tooltip: text("Report.Effectiveness.Tooltip") }),
        report.busiest ? Row({ key: "staff-share", left: text("Report.StaffShare", { SERIES: seriesName(report.busiest) }), right: percent(report.staffShare), subRow: true, tooltip: text("Report.StaffShare.Tooltip") }) : null,
        Row({ key: "lag", left: text("Report.Lag"), right: text("Report.LagValue", { DAYS: report.lagDays.toFixed(1) }), subRow: true }),
        report.fading >= 0 ? Row({ key: "fade", left: text("Report.Fading"), right: percent(report.fading), subRow: true, tooltip: text("Report.Fading.Tooltip") }) : null,
        report.capture >= 0.005 ? Row({ key: "cap", left: text("Report.Capture"), right: percent(report.capture), subRow: true, tooltip: text("Report.Capture.Tooltip") }) : null,
    ];

    const output = [];
    switch (report.mechanism) {
        case "PerformanceStandard":
        case "Generic":
            if (report.authority) {
                output.push(Row({ key: "det", left: text("Report.Detection"), right: percent(report.detection), subRow: true }));
                output.push(Row({ key: "comp", left: text("Report.Compliance"), right: percent(report.compliance), subRow: true }));
            }
            break;
        case "EmissionTax":
            output.push(Row({ key: "rep", left: text("Report.Reporting"), right: percent(report.compliance), subRow: true }));
            output.push(Row({ key: "rate", left: text("Report.EffectiveRate"), right: `${Math.round(report.effectiveRate)}%`, subRow: true }));
            output.push(Row({ key: "abate", left: text("Report.Abatement"), right: percent(report.abatement), subRow: true }));
            break;
        case "AbatementSubsidy":
            output.push(Row({ key: "abate", left: text("Report.Abatement"), right: percent(report.abatement), subRow: true }));
            break;
        case "Monitoring":
            output.push(Row({ key: "watch", left: text("Report.WatchDetection"), right: `${percent(report.detectionBefore)} → ${percent(report.detectionAfter)}`, subRow: true }));
            break;
        case "Disclosure":
            output.push(Row({ key: "vol", left: text("Report.Voluntary"), right: points(report.voluntaryGain), subRow: true }));
            break;
        case "EmissionTrading":
            output.push(Row({ key: "price", left: text("Report.AllowancePrice"), right: text("Report.AllowancePrice.Value", { PERCENT: percent(report.tradingPrice) }), subRow: true, tooltip: text("Report.AllowancePrice.Tooltip") }));
            output.push(Row({ key: "other", left: text("Report.OtherCut"), right: percent(report.tradingOther), subRow: true, tooltip: text("Report.OtherCut.Tooltip") }));
            output.push(Row({ key: "evade", left: text("Report.NoAllowance"), right: percent(1 - report.compliance), subRow: true }));
            output.push(Row({ key: "abate", left: text("Report.Abatement"), right: percent(report.tradingCut), subRow: true }));
            break;
        case "AllowanceAuction":
            output.push(Row({ key: "share", left: text("Report.AuctionShare"), right: percent(report.intensity), subRow: true }));
            output.push(Row({ key: "price", left: text("Report.AllowancePrice"), right: text("Report.AllowancePrice.Value", { PERCENT: percent(report.tradingPrice) }), subRow: true, tooltip: text("Report.AllowancePrice.Tooltip") }));
            break;
    }
    output.push(Row({ key: "prog", left: text("Report.Progress"), right: percent(report.progress), subRow: true, tooltip: text(report.inflated ? "Report.Inflated.Tooltip" : "Report.Progress.Tooltip") }));
    // Performance management inflates the figure above (§14.11 option B): say so right under it.
    if (report.inflated) {
        output.push(React.createElement("div", { key: "inflated", style: { color: "var(--warningColor)", padding: "4rem 10rem" } }, text("Report.Inflated")));
    }
    // City conditions (docs/POLICY-REVIEW.md): a plain sentence, no factor (2026-11-10 user decision).
    if (report.condition !== undefined && Math.abs(report.condition - 1) >= 0.1 && report.progress > 0) {
        output.push(React.createElement("div", { key: "condition", style: { color: "var(--normalTextColorDim, inherit)", padding: "4rem 10rem" } },
            text(report.condition > 1 ? "Report.Condition.Up" : "Report.Condition.Down")));
    }

    // Outcome: air and ground pollution of the same size read as one row.
    const outcome = [];
    const mods = report.modifiers.slice();
    const air = mods.find((m) => m.type === "IndustrialAirPollution");
    const ground = mods.find((m) => m.type === "IndustrialGroundPollution");
    if (air && ground && Math.abs(air.value - ground.value) < 1e-4) {
        mods.splice(mods.indexOf(ground), 1);
        air.type = "IndustrialPollution";
    }
    for (const m of mods) {
        // Less pollution, garbage and cost is good; more efficiency, attraction and safety is good.
        const higherIsGood = /(Efficiency|Entertainment|Safety|Interest|Graduation|WarningTime)$/.test(m.type);
        const good = higherIsGood ? m.value > 0 : m.value < 0;
        // Absolute modifiers are amounts (the early warning adds seconds), not percentages.
        const shown = m.absolute
            ? (m.value >= 0 ? "+" : "") + (m.type === "PrisonTime" ? text("Second.Months", { N: m.value.toFixed(1) }) : text("Report.Seconds", { SECONDS: Math.round(m.value) }))
            : signedPercent(m.value);
        outcome.push(Row({ key: `mod-${m.type}`, left: text(`Report.Modifier.${m.type}`), right: React.createElement("span", { style: { color: m.value === 0 ? undefined : good ? "var(--positiveColor)" : "var(--negativeColor)" } }, shown), subRow: true }));
    }
    for (const m of report.money.filter((r) => r.income)) {
        outcome.push(Row({ key: `income-${m.row}`, left: translate(`EconomyPanel.BUDGET_ITEM[${m.row}]`, m.row), right: money(m.monthly, true), subRow: true }));
    }
    for (const l of report.levers) {
        outcome.push(Row({ key: `lever-${l.key}`, left: text(`Report.Lever.${l.key.split(".").pop()}`), right: signedPercent(l.value), subRow: true }));
    }
    // Administrative reforms: what they do to the administrative layer (§14.10, option A).
    for (const r of report.reforms || []) {
        const shown = r.unit === 0 ? signedPercent(r.value)
            : r.unit === 2 ? text("Report.Reform.Hours", { HOURS: (r.value >= 0 ? "+" : "") + r.value.toFixed(0) })
            : r.unit === 3 ? text("Report.Reform.Count", { COUNT: (r.value >= 0 ? "+" : "") + r.value.toFixed(1) })
            : (r.value >= 0 ? "+" : "") + r.value.toFixed(2);
        const tone = Math.abs(r.value) < 1e-4 ? undefined : r.good ? "var(--positiveColor)" : "var(--negativeColor)";
        outcome.push(Row({ key: `reform-${r.key}`, left: text(`Report.Reform.${r.key}`), right: React.createElement("span", { style: { color: tone } }, shown),
            tooltip: text(`Report.Reform.${r.key}.Tooltip`), subRow: true }));
    }
    if (report.hasAir) {
        outcome.push(Row({ key: "air", left: text("Report.Air"), right: React.createElement("span", { style: { color: report.airChange < 0 ? "var(--positiveColor)" : report.airChange > 0 ? "var(--negativeColor)" : undefined } }, signedPercent(report.airChange)), subRow: true }));
    }

    const section = (key, title, rows) => React.createElement(InfoSection, { key, disableFocus: true },
        Row({ left: text(title), uppercase: true }), ...rows.filter(Boolean));
    sections.push(section("input", "Report.Input", input));
    sections.push(section("activity", "Report.Activity", activity));
    sections.push(section("output", "Report.Output", output));
    sections.push(section("outcome", "Report.Outcome", outcome));

    const notes = [];
    if (report.suspended) {
        notes.push(React.createElement("div", { key: "susp", style: { color: "var(--warningColor)", padding: "4rem 10rem" } }, text("Report.Suspended")));
    }
    if (report.warning) {
        notes.push(React.createElement("div", { key: "warn", style: { color: "var(--warningColor)", padding: "4rem 10rem" } }, text("Report.Warning")));
    }
    if (report.noWelfare) {
        notes.push(React.createElement("div", { key: "nowelfare", style: { color: "var(--warningColor)", padding: "4rem 10rem" } }, text("Report.NoWelfare")));
    }
    const suit = suits && suits.suits ? suits.suits.find((s) => s.policyId === id) : null;
    if (suit) {
        notes.push(React.createElement("div", { key: "suit", style: { padding: "4rem 10rem" } },
            text("Report.InCourt", { CASE: caseNo(suit.year, suit.number) }), " ",
            ui && ui.Button
                ? React.createElement(ui.Button, { variant: "text", onSelect: () => api.trigger("game", "showCityInfoPanel", LAWSUITS_TAB) }, text("Report.OpenLawsuits"))
                : null));
    }
    if (notes.length) {
        sections.push(React.createElement(InfoSection, { key: "notes", disableFocus: true }, ...notes));
    }
    return React.createElement("div", { className: "rp-policy-report" }, sections);
}

// The detail setting under "세부 설정": the game's policy row draws one slider only (2026-11-10 user decision).
function SecondSection({ id, second, locked }) {
    const text = useText();
    const [value, setValue] = React.useState(second.value);
    // Follow the saved value when it changes from elsewhere (a court ruling, a load).
    React.useEffect(() => setValue(second.value), [second.value]);
    const transformer = useStepTransformer ? useStepTransformer(second.step) : undefined;
    const shown = second.unit === "money" ? `¢${groupDigits(value)}`
        : second.unit === "months" ? text("Second.Months", { N: Math.round(value) })
        : second.unit === "minutes" ? text("Second.Minutes", { N: Math.round(value) })
        : second.unit === "percent" ? `${Math.round(value)}%`
        : `${Math.round(value)}`;
    const change = (v) => {
        const stepped = Math.min(second.max, Math.max(second.min, Math.round(v / second.step) * second.step));
        setValue(stepped);
        api.trigger(GROUP, "setPolicySecond", id, stepped);
    };
    return React.createElement(InfoSection, { disableFocus: true },
        Row({ left: text("Second.Title"), uppercase: true }),
        Row({ left: text(`Second.${second.key}`), right: shown, subRow: true }),
        Slider && !locked ? React.createElement("div", { style: { padding: "4rem 10rem" } },
            React.createElement(Slider, { value, start: second.min, end: second.max, valueTransformer: transformer, onChange: change })) : null,
        React.createElement("div", { style: { padding: "2rem 10rem 6rem", fontSize: "12rem", opacity: 0.7 } },
            text(locked ? "Second.Locked" : `Second.${second.key}.Hint`)));
}

// ---- Lawsuits tab

function LawsuitsPage() {
    const data = api.useValue(lawsuits$);
    const text = useText();
    const content = !data ? null : [
        data.settlementResult === "Accepted" && !data.suits.some((s) => s.key === data.settlementKey)
            ? React.createElement("div", { key: "settled", style: { color: "var(--positiveColor)", padding: "8rem 10rem" } }, text("Suit.Settle.Accepted"))
            : null,
        data.suits.length === 0
            ? React.createElement("div", { key: "none", style: { padding: "20rem 10rem", opacity: 0.7 } }, text("Suit.None"))
            : null,
        ...data.suits.map((suit) => React.createElement(SuitSection, { key: suit.key, suit, data })),
        data.bans.length || data.debts.length ? React.createElement(AfterVerdict, { key: "after", data }) : null,
    ];
    const body = Scrollable
        ? React.createElement(Scrollable, { style: { flex: "1 1 auto" } }, content)
        : React.createElement("div", { style: { flex: "1 1 auto", overflowY: "auto" } }, content);
    return React.createElement("div", { className: "rp-lawsuits", style: { display: "flex", flexDirection: "column", width: "100%", height: "100%", minHeight: "560rem" } }, body);
}

function SuitSection({ suit, data }) {
    const text = useText();
    const money = useMoney();
    const formatName = useNameFormat ? useNameFormat() : null;
    const { translate } = l10n.useLocalization();
    const [open, setOpen] = React.useState(testOpenClaims);
    const korean = text("Lang") === "ko";
    const policy = translate(`Policy.TITLE[${suit.policyId}]`, suit.policyId);
    const value = (v) => suit.slider ? policyValue(suit.policyId, v, false) : `${Math.round(v)}`;
    const names = defendants(text, korean, data.cityName, data.government);
    const caseNo = text("Suit.CaseNumber", { YEAR: suit.year, N: suit.number });
    const titleKey = `Suit.Title${suit.againstCut ? ".Cut" : ""}${suit.claim > 0 ? "" : ".NoClaim"}`;

    const representative = suit.representative && formatName ? formatName(suit.representative) : "";
    const others = Math.max(0, suit.plaintiffs - 1);
    const plaintiffs = others > 0 ? text(`Plaintiffs.${suit.group}`, { NAME: representative, N: groupDigits(others) }) : representative;

    const rows = [
        Row({ key: "p", left: text("Suit.Plaintiffs"), right: plaintiffs }),
        Row({ key: "d", left: text("Suit.Defendants"), right: `${names.holder}, ${names.body}` }),
        Row({ key: "t", left: text("Suit.UntilVerdict"), right: text("Suit.HoursLeft", { HOURS: Math.ceil(suit.remainingHours) }) }),
        Row({ key: "w", left: text("Suit.WinChance"), right: text(`Suit.Hint.${suit.hint}`), tooltip: text("Suit.WinChance.Tooltip") }),
    ];
    if (suit.response === 0) {
        rows.push(Row({ key: "r", left: text("Suit.Response"), right: React.createElement("span", { style: { color: "var(--warningColor)" } }, text("Suit.Response.None")), tooltip: text("Suit.Response.None.Tooltip") }));
    } else {
        rows.push(Row({ key: "r", left: text("Suit.Response"), right: suit.response === 1 ? text("Suit.Response.Lawyer", { FEE: money(suit.lawyerFee) }) : text("Suit.Response.Official") }));
    }
    // Injunction stages: 0 not requested, 1 requested, 2 granted, 3 in effect, 4 denied, 5 cancelled.
    const injunction = [null, text("Suit.Injunction.Requested"), text("Suit.Injunction.Granted", { MINUTES: Math.max(1, Math.ceil(suit.injunctionHours * 60)) }),
        text("Suit.Injunction.InEffect"), text("Suit.Injunction.Denied"), text("Suit.Injunction.Cancelled")][suit.injunction];
    if (injunction) {
        const warn = suit.injunction === 2 || suit.injunction === 3;
        rows.push(Row({ key: "i", left: text("Suit.Injunction"), right: React.createElement("span", { style: warn ? { color: "var(--warningColor)" } : null }, injunction),
            tooltip: suit.injunction === 2 && suit.response === 0 ? text("Suit.Injunction.Granted.Tooltip") : undefined }));
    }

    const claims = [];
    if (open) {
        const date = text("Claim.Date", { YEAR: suit.actYear, MONTH: suit.actMonth });
        const args = {
            DATE: date, POLICY: policy, VALUE: value(suit.contested), PRIOR: value(suit.prior), HOLDER: names.holder, BODY: names.body, CLAIM: money(suit.claim),
            HOLDER_SUBJECT: josa(names.holder, "이", "가"), BODY_TOPIC: josa(names.body, "은", "는"),
            POLICY_OBJECT: josa(policy, "을", "를"), CLAIM_OBJECT: josa(money(suit.claim), "을", "를"),
        };
        const first = suit.againstCut ? (suit.slider ? "Claim.Cut" : "Claim.CutToggle") : (suit.slider ? "Claim.Burden" : "Claim.BurdenToggle");
        const lines = [text(first, args)];
        if (suit.claim > 0) {
            lines.push(text("Claim.Pay", args));
        }
        lines.push(text("Claim.Costs", args));
        const grounds = [["Proportionality", 1], ["LegitimateExpectation", 2], ["Procedure", 4]].filter(([, bit]) => (suit.grounds & bit) !== 0);
        claims.push(React.createElement("div", { key: "claims", style: { padding: "4rem 10rem 8rem 20rem", lineHeight: 1.5, opacity: 0.9 } },
            React.createElement("div", { style: { opacity: 0.7 } }, text("Suit.Claims")),
            ...lines.map((line, i) => React.createElement("div", { key: `c${i}` }, line)),
            React.createElement("div", { style: { opacity: 0.7, marginTop: "6rem" } }, text("Suit.Grounds")),
            ...grounds.map(([name]) => React.createElement("div", { key: name }, `· ${text(`Suit.Ground.${name}`)}`))));
    }
    const toggle = ui && ui.Button
        ? React.createElement(ui.Button, { key: "toggle", variant: "text", onSelect: () => setOpen(!open), style: { padding: "4rem 10rem" } }, text(open ? "Suit.HideClaims" : "Suit.ShowClaims"))
        : null;

    const actions = suit.response === 0
        ? React.createElement("div", { key: "actions", style: { display: "flex", padding: "6rem 10rem", gap: "6rem" } },
            ActionButton(text("Suit.Hire"), text(suit.late ? "Suit.Hire.Late" : "Suit.Hire.Detail", { FEE: money(suit.lawyerFee) }), () => api.trigger(GROUP, "respondSuit", suit.key, 1)),
            ActionButton(text("Suit.Assign"), text("Suit.Assign.Detail", { N: suit.officials }), () => api.trigger(GROUP, "respondSuit", suit.key, 2)))
        : React.createElement(SettlementRows, { key: "settle", suit, data });

    return React.createElement(InfoSection, { disableFocus: true },
        React.createElement("div", { style: { padding: "8rem 10rem 4rem" } },
            React.createElement("div", { style: { opacity: 0.6, fontSize: "var(--fontSizeS)" } }, caseNo),
            React.createElement("div", { style: { fontSize: "var(--fontSizeL)", fontWeight: "bold", marginTop: "2rem" } }, text(titleKey, { POLICY: policy }))),
        ...rows, toggle, ...claims, actions);
}

function ActionButton(title, detail, onSelect) {
    const children = [
        React.createElement("div", { key: "t" }, title),
        React.createElement("div", { key: "d", style: { opacity: 0.65, fontSize: "var(--fontSizeS)", marginTop: "2rem" } }, detail),
    ];
    const style = { flex: "1 1 0", padding: "8rem 10rem", textAlign: "left", display: "block" };
    return ui && ui.Button
        ? React.createElement(ui.Button, { key: title, variant: "flat", onSelect, style }, children)
        : React.createElement("button", { key: title, onClick: onSelect, style }, children);
}

// Settlement: a policy level between the contested and the earlier one (a switch for toggles), plus compensation.
function SettlementRows({ suit, data }) {
    const text = useText();
    const lo = Math.min(suit.contested, suit.prior);
    const hi = Math.max(suit.contested, suit.prior);
    const [level, setLevel] = React.useState(suit.contested);
    const [compensation, setCompensation] = React.useState(0);
    const transformer = useStepTransformer ? useStepTransformer(1) : undefined;
    const left = data.maxOffers - suit.offers;
    const waiting = suit.waitHours > 0;
    const refused = data.settlementKey === suit.key && data.settlementResult === "Refused";

    let control;
    if (suit.slider && Slider) {
        control = React.createElement("div", { style: { display: "flex", alignItems: "center" } },
            React.createElement("div", { style: { width: "60rem", textAlign: "right", marginRight: "12rem" } }, policyValue(suit.policyId, level, false)),
            React.createElement("div", { style: { width: "200rem" } },
                React.createElement(Slider, { value: level, start: lo, end: hi, valueTransformer: transformer, onChange: setLevel })));
    } else if (Checkbox) {
        const changed = level !== suit.contested;
        control = React.createElement(Checkbox, { checked: changed, onChange: () => setLevel(changed ? suit.contested : suit.prior) });
    }
    const label = !suit.slider ? text(suit.againstCut ? "Suit.Settle.Restore" : "Suit.Settle.TurnOff") : text("Suit.Settle.Intensity");
    const input = IntInput
        ? React.createElement(IntInput, { value: compensation, min: 0, onChange: (v) => setCompensation(Math.max(0, v | 0)), style: { width: "140rem" } })
        : null;
    const buttonText = left <= 0 ? text("Suit.Settle.NoneLeft") : waiting ? text("Suit.Settle.Wait", { HOURS: Math.ceil(suit.waitHours) }) : text("Suit.Settle.Offer");
    const disabled = left <= 0 || waiting;
    const button = ui && ui.Button
        ? React.createElement(ui.Button, { variant: "flat", disabled, onSelect: disabled ? undefined : () => api.trigger(GROUP, "proposeSettlement", suit.key, level, compensation),
            style: { padding: "8rem 10rem", margin: "6rem 10rem", textAlign: "center", opacity: disabled ? 0.5 : 1 } }, buttonText)
        : null;
    return React.createElement("div", null,
        Row({ left: text("Suit.Settle"), right: text("Suit.Settle.Left", { N: Math.max(0, left) }), tooltip: text("Suit.Settle.Tooltip") }),
        Row({ left: label, right: control, subRow: true }),
        Row({ left: text("Suit.Settle.Compensation"), right: input, subRow: true }),
        refused ? React.createElement("div", { style: { color: "var(--warningColor)", padding: "4rem 10rem" } }, text("Suit.Settle.Refused")) : null,
        button);
}

function AfterVerdict({ data }) {
    const text = useText();
    const money = useMoney();
    const { translate } = l10n.useLocalization();
    const caseNo = (year, number) => text("Suit.CaseNumber", { YEAR: year, N: number });
    const rows = [Row({ key: "h", left: text("Suit.After"), uppercase: true })];
    for (const ban of data.bans) {
        rows.push(Row({ key: `ban-${ban.policyId}`, left: translate(`Policy.TITLE[${ban.policyId}]`, ban.policyId),
            right: ban.number > 0 ? text(ban.partial ? "Suit.LostPart" : "Suit.Lost", { CASE: caseNo(ban.year, ban.number) }) : "" }));
        rows.push(Row({ key: `ban-days-${ban.policyId}`, left: text("Suit.Ban"), subRow: true,
            right: React.createElement("span", { style: { color: "var(--warningColor)" } }, text("Report.DaysLeft", { DAYS: ban.daysLeft.toFixed(1) })),
            tooltip: text(ban.toggle ? "Suit.Ban.Toggle.Tooltip" : "Suit.Ban.Slider.Tooltip") }));
    }
    for (const debt of data.debts) {
        rows.push(Row({ key: `debt-${debt.year}-${debt.number}`, left: debt.number > 0 ? `${text("Suit.Debt")} (${caseNo(debt.year, debt.number)})` : text("Suit.Debt"),
            right: money(debt.remaining), tooltip: text("Suit.Debt.Tooltip", { CAP: money(debt.dailyCap), ARREARS: money(debt.arrears) }) }));
    }
    return React.createElement(InfoSection, { disableFocus: true }, ...rows);
}


// ---- Protests tab (docs/PLAN.md §12 "화면", docs/previews/protest-panel.html)

const TRAITS = ["Peaceful", "General", "Hardline", "Extreme"];

function ProtestsPage() {
    const data = api.useValue(protests$);
    const text = useText();
    const list = data && data.list ? data.list : [];
    const actions = data && data.actions ? data.actions : [];
    const content = list.length === 0 && actions.length === 0
        ? [React.createElement("div", { key: "none", style: { padding: "20rem 10rem", opacity: 0.7 } }, text("Protest.None"))]
        : list.map((p) => React.createElement(ProtestSection, { key: p.id, protest: p }))
            .concat(actions.map((a) => React.createElement(ActionSection, { key: `action-${a.id}`, action: a })));
    const body = Scrollable
        ? React.createElement(Scrollable, { style: { flex: "1 1 auto" } }, content)
        : React.createElement("div", { style: { flex: "1 1 auto", overflowY: "auto" } }, content);
    return React.createElement("div", { className: "rp-protests", style: { display: "flex", flexDirection: "column", width: "100%", height: "100%", minHeight: "560rem" } }, body);
}

// The policy's value as the policy tab shows it (percent for percentage sliders), or the residential tax rate.
// Sliders not in percent (2026-11-10 user decision; same units as in PolicyDefinitions): money a household gets a month,
// and the months benefits are paid longer.
const POLICY_UNITS = {
    "RealisticPolicies.LivelihoodSupport": "money",
    "RealisticPolicies.RentSupport": "money",
    "RealisticPolicies.UnemploymentDuration": "integer",
    "RealisticPolicies.HarsherSentencing": "integer",
    "RealisticPolicies.DisasterWarning": "integer",
};

// A policy level as its slider shows it: ¢ for money, a plain number, or percent.
function policyValue(policyId, value, toggle) {
    if (toggle) {
        return `${Math.round(value)}`;
    }
    const unit = POLICY_UNITS[policyId];
    if (unit === "money") {
        return `¢${groupDigits(value)}`;
    }
    return unit === "integer" ? `${Math.round(value)}` : `${Math.round(value)}%`;
}

function protestValue(protest, value) {
    if (!protest.policyId) {
        return `${Math.round(value)}%`;
    }
    return policyValue(protest.policyId, value, protest.toggle);
}

// Legitimacy cost of repression scales with (1 - intensity)^2: a word, as the lawsuit hint.
function legitimacyWord(text, protest, disperse) {
    const v = (disperse ? 1 : 0.2) * Math.pow(1 - protest.intensity, 2);
    return text(v >= 0.3 ? "Protest.Legitimacy.High" : v >= 0.1 ? "Protest.Legitimacy.Medium" : "Protest.Legitimacy.Low");
}

// Strike or collective closure (§12 "파업"): the same rows as a protest card. No response buttons: people keep going to
// work and output falls; giving the policy back ends it, which the tooltip says.
const ACTION_SECTORS = ["Industrial", "Office", "Public", "Commercial"];

function ActionSection({ action }) {
    const text = useText();
    const { translate } = l10n.useLocalization();
    const policy = translate(`Policy.TITLE[${action.policyId}]`, action.policyId);
    const title = text(action.kind === 0 ? "Action.Title.Strike" : "Action.Title.Closure", { POLICY: policy });
    const demand = action.toggle
        ? text(action.demand > 0.5 ? "Protest.Demand.On" : "Protest.Demand.Off", { POLICY: policy, POLICY_OBJECT: josa(policy, "을", "를") })
        : text("Protest.Demand.Value", { POLICY: policy, POLICY_OBJECT: josa(policy, "을", "를"), VALUE: protestValue(action, action.demand) });
    const sector = ACTION_SECTORS[action.sector] || "Industrial";
    const rows = [
        Row({ key: "demand", left: text("Protest.Demand"), right: demand }),
        Row({ key: "sector", left: text("Action.Sector"), right: text(`Action.Sector.${sector}`) }),
        Row({ key: "share", left: text("Action.Share"), right: percent(action.share) }),
        Row({ key: "effect", left: text("Action.Effect"),
            right: React.createElement("span", { style: { color: "var(--negativeColor)" } }, text(`Action.Effect.${sector}`, { N: Math.max(1, Math.round(action.loss * 100)) })),
            tooltip: text(action.kind === 0 ? "Action.Effect.Tooltip.Strike" : "Action.Effect.Tooltip.Closure") }),
        Row({ key: "hours", left: text("Protest.Hours"), right: text("Protest.Hours.Value", { HOURS: Math.max(1, Math.round(action.hours)) }) }),
    ];
    return React.createElement(InfoSection, { disableFocus: true },
        React.createElement("div", { style: { padding: "8rem 10rem 4rem" } },
            React.createElement("div", { style: { fontSize: "var(--fontSizeL)", fontWeight: "bold" } }, title)),
        ...rows);
}

// inBuilding: the section in the info panel of the building the protest icon sits on (docs/previews/protest-building-panel.html,
// option B): the same rows, no response buttons, a link to the protests tab with the hint in its tooltip.
function ProtestSection({ protest, inBuilding, focusKey }) {
    const text = useText();
    const { translate } = l10n.useLocalization();
    const policy = protest.policyId ? translate(`Policy.TITLE[${protest.policyId}]`, protest.policyId) : text("Protest.Tax");
    const title = protest.policyId ? text(protest.cut ? "Protest.Title.Cut" : "Protest.Title.Burden", { POLICY: policy }) : text("Protest.Title.Tax");
    // State after the title in coloured text, as the other tabs show states.
    const state = protest.granted ? text(protest.riot ? "Protest.Dispersing" : "Protest.Dissolved") : protest.riot ? text("Protest.Riot") : null;
    const tags = state ? [React.createElement("span", {
        key: "state",
        style: { marginLeft: "10rem", fontWeight: "normal", fontSize: "var(--fontSizeM)", color: protest.granted ? "var(--positiveColor)" : "var(--negativeColor)" },
    }, state)] : [];

    let demand;
    if (!protest.policyId) {
        demand = text("Protest.Demand.Tax", { VALUE: protestValue(protest, protest.demand) });
    } else if (protest.toggle) {
        demand = text(protest.demand > 0.5 ? "Protest.Demand.On" : "Protest.Demand.Off", { POLICY: policy, POLICY_OBJECT: josa(policy, "을", "를") });
    } else {
        demand = text("Protest.Demand.Value", { POLICY: policy, POLICY_OBJECT: josa(policy, "을", "를"), VALUE: protestValue(protest, protest.demand) });
    }
    // A plain value like the other rows (no bar, docs/PLAN.md §10 "모양"); the riot threshold is in the tooltip.
    const intensity = Math.round(protest.intensity * 100);
    const intensityValue = React.createElement("span", { style: { color: protest.riot ? "var(--negativeColor)" : undefined } }, `${intensity}%`);

    const rows = [
        Row({ key: "demand", left: text("Protest.Demand"), right: demand }),
        Row({ key: "people", left: text("Protest.People"), right: text("Protest.People.Value", { N: groupDigits(protest.people), SHARE: percent(protest.share) }) }),
        Row({ key: "intensity", left: text("Protest.Intensity"), right: intensityValue, tooltip: text("Protest.Intensity.Tooltip") }),
        Row({ key: "trait", left: text("Protest.Trait"), right: text(`Protest.Trait.${TRAITS[protest.trait] || "General"}`), tooltip: text("Protest.Trait.Tooltip") }),
        Row({ key: "hours", left: text("Protest.Hours"), right: text("Protest.Hours.Value", { HOURS: Math.max(1, Math.round(protest.hours)) }) }),
        Row({ key: "closed", left: text("Protest.Closure"),
            right: protest.closed ? React.createElement("span", { style: { color: "var(--warningColor)" } }, text("Protest.Closure.On", { N: protest.segments }))
                : text("Protest.Closure.Off"), tooltip: text("Protest.Closure.Tooltip") }),
    ];
    if (protest.riot) {
        rows.push(Row({ key: "crime", left: text("Protest.Crime"), right: React.createElement("span", { style: { color: "var(--negativeColor)" } }, `${intensity}%`), tooltip: text("Protest.Crime.Tooltip") }));
    }
    const responses = ["Protest.Response.None", "Protest.Response.Negotiate", "Protest.Response.Watch", "Protest.Response.Disperse", "Protest.Response.Ignore"];
    rows.push(Row({ key: "response", left: text("Protest.Response"),
        right: protest.response === 0 ? React.createElement("span", { style: { color: "var(--warningColor)" } }, text(responses[0])) : text(responses[protest.response]),
        tooltip: protest.response === 0 ? text("Protest.Response.None.Tooltip") : protest.response === 4 && !protest.riot ? text("Protest.Ignore.Detail") : undefined }));

    let actions;
    if (protest.granted) {
        actions = React.createElement("div", { style: { color: "var(--positiveColor)", padding: "6rem 10rem" } },
            protest.riot ? text("Protest.Granted.Riot", { HOURS: protest.riotHoursLeft.toFixed(1) }) : text("Protest.Granted"));
    } else if (inBuilding) {
        const link = ui && ui.Button
            ? React.createElement(ui.Button, { variant: "text", onSelect: () => api.trigger("game", "showCityInfoPanel", PROTESTS_TAB) }, text("Protest.OpenTab"))
            : text("Protest.OpenTab");
        actions = Row({ key: "open-tab", left: link, tooltip: text("Protest.OpenTab.Tooltip") });
    } else if (protest.response === 0) {
        const watchCars = Math.max(1, Math.ceil(protest.people * protest.intensity / 20 * 0.5));
        const disperseCars = Math.max(1, Math.ceil(protest.people * protest.intensity / 20));
        actions = React.createElement("div", { style: { display: "flex", flexWrap: "wrap", padding: "6rem 10rem", gap: "6rem" } },
            ActionButton(text("Protest.Negotiate"), text("Protest.Negotiate.Detail"), () => api.trigger(GROUP, "respondProtest", protest.id, 1)),
            ActionButton(text("Protest.Watch"), text("Protest.Repress.Detail", { N: watchCars, WORD: legitimacyWord(text, protest, false) }), () => api.trigger(GROUP, "respondProtest", protest.id, 2)),
            ActionButton(text("Protest.Disperse"), text("Protest.Repress.Detail", { N: disperseCars, WORD: legitimacyWord(text, protest, true) }), () => api.trigger(GROUP, "respondProtest", protest.id, 3)),
            ActionButton(text("Protest.Ignore"), text(protest.riot ? "Protest.Ignore.Riot" : "Protest.Ignore.Detail"), () => api.trigger(GROUP, "respondProtest", protest.id, 4)));
    } else {
        const parts = [];
        if (protest.response === 1) {
            parts.push(React.createElement(NegotiationRows, { key: "neg", protest, policy }));
        } else if (protest.response === 2 || protest.response === 3) {
            const ratio = protest.carsNeeded > 0 ? Math.min(1, protest.carsArrived / protest.carsNeeded) : 0;
            parts.push(Row({ key: "cars", left: text("Protest.Cars"), right: text("Protest.Cars.Value", { IN: protest.carsArrived, N: protest.carsNeeded }), tooltip: text("Protest.Cars.Tooltip") }));
            parts.push(Row({ key: "effect", left: text("Protest.Effect"), right: percent(ratio * (protest.response === 3 ? 1 : 0.5)), tooltip: text("Protest.Effect.Tooltip") }));
            parts.push(Row({ key: "legit", left: text("Protest.Legitimacy"), right: legitimacyWord(text, protest, protest.response === 3), tooltip: text("Protest.Legitimacy.Tooltip") }));
            if (protest.carsArrived < protest.carsNeeded) {
                parts.push(React.createElement("div", { key: "short", style: { color: "var(--warningColor)", padding: "4rem 10rem" } }, text("Protest.Cars.Short")));
            }
        } else if (protest.response === 4 && protest.riot) {
            // Only the warning stays on screen; the plain note is the response row's tooltip.
            parts.push(React.createElement("div", { key: "ign", style: { color: "var(--warningColor)", padding: "4rem 10rem" } }, text("Protest.Ignore.Riot")));
        }
        if (ui && ui.Button) {
            parts.push(React.createElement(ui.Button, { key: "change", variant: "text", onSelect: () => api.trigger(GROUP, "respondProtest", protest.id, 0), style: { padding: "4rem 10rem" } }, text("Protest.Change")));
        }
        actions = React.createElement("div", null, ...parts);
    }

    const head = inBuilding ? [Row({ key: "head", left: text("Tab.Protests"), uppercase: true })] : [];
    return React.createElement(InfoSection, inBuilding ? { focusKey, disableFocus: true } : { disableFocus: true },
        ...head,
        React.createElement("div", { style: { padding: "8rem 10rem 4rem" } },
            React.createElement("div", { style: { opacity: 0.6, fontSize: "var(--fontSizeS)" } }, text("Protest.Segments", { N: protest.segments })),
            React.createElement("div", { style: { display: "flex", flexDirection: "row", alignItems: "center", fontSize: "var(--fontSizeL)", fontWeight: "bold", marginTop: "2rem" } },
                React.createElement("span", { key: "title" }, title), ...tags)),
        ...rows, actions);
}

// Info panel section of the park or road the protest icon sits on (C#: ProtestInfoSection).
function ProtestBuildingSection(props) {
    return props.protest ? React.createElement(ProtestSection, { protest: props.protest, inBuilding: true, focusKey: props.focusKey }) : null;
}

// Negotiation: the policy moved toward the demand (a switch for toggles; the residential tax for a tax protest) and a payment.
function NegotiationRows({ protest, policy }) {
    const text = useText();
    const lo = Math.min(protest.now, protest.demand);
    const hi = Math.max(protest.now, protest.demand);
    const [level, setLevel] = React.useState(protest.now);
    const [compensation, setCompensation] = React.useState(0);
    const transformer = useStepTransformer ? useStepTransformer(protest.step || 1) : undefined;
    let control;
    if (!protest.toggle && Slider && hi > lo) {
        control = React.createElement("div", { style: { display: "flex", alignItems: "center" } },
            React.createElement("div", { style: { width: "60rem", textAlign: "right", marginRight: "12rem" } }, protestValue(protest, level)),
            React.createElement("div", { style: { width: "200rem" } },
                React.createElement(Slider, { value: level, start: lo, end: hi, valueTransformer: transformer, onChange: setLevel, style: { width: "200rem" } })));
    } else if (protest.toggle && Checkbox) {
        const changed = level !== protest.now;
        control = React.createElement(Checkbox, { checked: changed, onChange: () => setLevel(changed ? protest.now : protest.demand) });
    } else {
        control = protestValue(protest, level);
    }
    const input = IntInput
        ? React.createElement(IntInput, { value: compensation, min: 0, onChange: (v) => setCompensation(Math.max(0, v | 0)), style: { width: "140rem" } })
        : null;
    const button = ui && ui.Button
        ? React.createElement(ui.Button, { variant: "flat", onSelect: () => api.trigger(GROUP, "negotiateProtest", protest.id, level, compensation),
            style: { padding: "8rem 10rem", margin: "6rem 10rem", textAlign: "center" } }, text("Protest.Apply"))
        : null;
    return React.createElement("div", null,
        Row({ left: protest.policyId ? policy : text("Protest.Tax"), right: control, subRow: true }),
        Row({ left: text("Protest.Compensation"), right: input, subRow: true, tooltip: text("Protest.Compensation.Tooltip") }),
        button);
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
        check("Game search field component found", !!SearchField && !!glossarySearchTheme);

        api.trigger("game", "showCityInfoPanel", 1);
        await sleep(3000);
        check("Search box is rendered on the policy page", !!document.querySelector(".rp-policy-search"));
        const list = document.querySelector(".rp-policy-list");
        const column = list && list.parentElement;
        const page = list && list.firstElementChild;
        const listRect = list ? list.getBoundingClientRect() : null;
        const columnRect = column ? column.getBoundingClientRect() : null;
        const pageRect = page ? page.getBoundingClientRect() : null;
        check("The policy list ends inside the panel under the search box (not below it)",
            !!listRect && !!columnRect && !!pageRect && pageRect.bottom <= listRect.bottom + 1 && listRect.bottom <= columnRect.bottom + 1,
            listRect && columnRect && pageRect ? `page bottom ${Math.round(pageRect.bottom)}, list bottom ${Math.round(listRect.bottom)}, column bottom ${Math.round(columnRect.bottom)}` : "elements missing");
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

        api.trigger("game", "closeActivePanel");
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
        api.trigger("game", "closeActivePanel");
    } catch (e) {
        check("UI self-test ran without errors", false, String(e && e.stack ? e.stack : e));
    }
    report("done");
}

async function runWelfareTest(translate) {
    try {
        await sleep(2500);
        const label = translate("RealisticPolicies.Series.Header", "");
        const text = document.body ? document.body.textContent || "" : "";
        check("Welfare office panel shows the series dropdown", !!label && text.indexOf(label) >= 0, label);
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

async function runReportTest(translate) {
    try {
        testOpenClaims = true;
        api.trigger("game", "showCityInfoPanel", 2);
        await sleep(3000);
        const page = document.querySelector(".rp-lawsuits");
        const text = page ? page.textContent || "" : "";
        check("Lawsuits tab is rendered", !!page);
        const hire = translate("RealisticPolicies.UI.Suit.Settle", "");
        check("Lawsuits tab lists the pending suit with its settlement rows", !!hire && text.indexOf(hire) >= 0, text.substring(0, 200));
        const costs = translate("RealisticPolicies.UI.Claim.Costs", "");
        check("Claims are written out", !!costs && text.indexOf(costs.substring(3, 12)) >= 0);
        report("shot:lawsuits");
        await sleep(1500);
        testOpenClaims = false;

        // The pollution tax is on (and in court); the emission standard is banned by now.
        testSelectPolicy = "RealisticPolicies.PollutionTax";
        api.trigger("game", "showCityInfoPanel", 1);
        await sleep(3000);
        testSelectPolicy = null;
        const panel = document.querySelector(".rp-policy-report");
        const input = translate("RealisticPolicies.UI.Report.Input", "");
        check("Policy report is shown under the policy description", !!panel && (panel.textContent || "").indexOf(input) >= 0, panel ? (panel.textContent || "").substring(0, 200) : "none");
        report("shot:policy-report");
        await sleep(1500);
        api.trigger("game", "closeActivePanel");
    } catch (e) {
        check("Report UI test ran without errors", false, String(e && e.stack ? e.stack : e));
    }
    report("done");
}

async function runProtestTest(translate) {
    try {
        api.trigger("game", "showCityInfoPanel", PROTESTS_TAB);
        await sleep(3000);
        const page = document.querySelector(".rp-protests");
        const text = page ? page.textContent || "" : "";
        check("Protests tab is rendered", !!page);
        const policy = translate("Policy.TITLE[RealisticPolicies.Scholarships]", "");
        const demand = translate("RealisticPolicies.UI.Protest.Demand", "");
        check("Protests tab lists the protest with its demand", !!policy && text.indexOf(policy) >= 0 && !!demand && text.indexOf(demand) >= 0, text.substring(0, 240));
        report("shot:protests-tab");
        await sleep(1500);
        // Click the Negotiate button as a player would.
        const label = translate("RealisticPolicies.UI.Protest.Negotiate", "");
        const buttons = page ? Array.from(page.querySelectorAll("button")) : [];
        const button = buttons.find((b) => (b.textContent || "").indexOf(label) === 0);
        check("Negotiate button is found", !!button, `${buttons.length} buttons`);
        if (button) {
            if (button.click) {
                button.click();
            } else {
                button.dispatchEvent(new MouseEvent("click", { bubbles: true }));
            }
        }
        await sleep(2500);
        const after = document.querySelector(".rp-protests");
        const apply = translate("RealisticPolicies.UI.Protest.Apply", "");
        check("Negotiation rows (policy level, compensation, apply) appear", !!after && !!apply && (after.textContent || "").indexOf(apply) >= 0, after ? (after.textContent || "").substring(0, 240) : "none");
        report("shot:protest-negotiation");
        // Where the negotiation slider's parts sit (its thumb looked off in a screenshot).
        if (after) {
            const parts = Array.from(after.querySelectorAll("div")).filter((d) => /slider|track|range|thumb/i.test(String(d.className || "")));
            report("Slider parts: " + parts.map((d) => {
                const r = d.getBoundingClientRect();
                return `${String(d.className).split(" ")[0]} x${Math.round(r.left)} w${Math.round(r.width)} style.width=${d.style.width || "-"}`;
            }).join("; "));
            const slider = parts.find((d) => /^slider/.test(String(d.className || "")));
            check("Negotiation slider is drawn at full width", !!slider && slider.getBoundingClientRect().width > 100, slider ? `${Math.round(slider.getBoundingClientRect().width)} px` : "not found");
        }
        await sleep(1500);
        api.trigger("game", "closeActivePanel");
    } catch (e) {
        check("Protest UI test ran without errors", false, String(e && e.stack ? e.stack : e));
    }
    report("done");
}

async function runBuildingPanelTest(translate) {
    try {
        await sleep(3000);
        const text = document.body ? document.body.textContent || "" : "";
        const link = translate("RealisticPolicies.UI.Protest.OpenTab", "");
        const demand = translate("RealisticPolicies.UI.Protest.Demand", "");
        check("The building's info panel shows the protest with the link to the protests tab", !!link && text.indexOf(link) >= 0 && !!demand && text.indexOf(demand) >= 0, link);
        report("shot:protest-building-panel");
        await sleep(1500);
    } catch (e) {
        check("Building panel UI test ran without errors", false, String(e && e.stack ? e.stack : e));
    }
    report("done");
}

async function runActionTest(translate) {
    try {
        api.trigger("game", "showCityInfoPanel", PROTESTS_TAB);
        await sleep(3000);
        const page = document.querySelector(".rp-protests");
        const text = page ? page.textContent || "" : "";
        const pay = translate("Policy.TITLE[RealisticPolicies.ContractingOut]", "");
        const effect = translate("RealisticPolicies.UI.Action.Effect", "");
        check("The protests tab shows the strike and the closure cards", !!pay && text.indexOf(pay) >= 0 && !!effect && text.indexOf(effect) >= 0, text.substring(0, 240));
        report("shot:industrial-actions");
        await sleep(1500);
        api.trigger("game", "closeActivePanel");
    } catch (e) {
        check("Strike card UI test ran without errors", false, String(e && e.stack ? e.stack : e));
    }
    report("done");
}

// Detail settings (second sliders): shown in the report column of a selected policy, also while it is off.
async function showDetail(translate, id, shot) {
    api.trigger("game", "showCityInfoPanel", 1);
    await sleep(1500);
    const modRows = Array.from(visibleRows).filter((r) => r.indexOf("RealisticPolicies.") === 0).length;
    report(`Before searching for ${id}: ${visibleRows.size} rows drawn (${modRows} of the mod's ${lastReportCount}), its row drawn ${visibleRows.has(id)}`);
    // Search for the policy so its row is the one drawn, then select it.
    search.set(translate(`Policy.TITLE[${id}]`, id));
    testSelectPolicy = id;
    await sleep(2500);
    testSelectPolicy = null;
    report(`After searching "${search.query}": rows ${Array.from(visibleRows).join(",") || "none"}`);
    const panel = document.querySelector(".rp-policy-report");
    const text = panel ? panel.textContent || "" : "";
    report("shot:" + shot);
    await sleep(1500);
    search.set("");
    return text;
}

const squeeze = (t) => String(t).replace(/\s+/g, "");

async function runDetailTest(translate) {
    try {
        const title = translate("RealisticPolicies.UI.Second.Title", "");
        const livelihood = "RealisticPolicies.LivelihoodSupport";
        const label = translate("RealisticPolicies.UI.Second.LivelihoodIncome", "");
        const hint = translate("RealisticPolicies.UI.Second.LivelihoodIncome.Hint", "");
        let text = await showDetail(translate, livelihood, "detail-livelihood");
        api.trigger("game", "closeActivePanel");
        await sleep(1000);
        api.trigger("game", "showCityInfoPanel", 1);
        await sleep(2500);
        const modRows = Array.from(visibleRows).filter((r) => r.indexOf("RealisticPolicies.") === 0);
        check("Every policy of the mod has a row in the city policy list", lastReportCount > 0 && modRows.length === lastReportCount, `${modRows.length} rows, ${lastReportCount} policies`);
        api.trigger("game", "closeActivePanel");
        await sleep(1000);
        text = await showDetail(translate, livelihood, "detail-livelihood");
        check("Livelihood support shows its detail setting as 32% with its label and hint",
            !!title && text.indexOf(title) >= 0 && !!label && squeeze(text).indexOf(squeeze(label) + "32%") >= 0 && !!hint && text.indexOf(hint) >= 0,
            text.substring(0, 240));
        report(`Slider parts in the report column: ${document.querySelectorAll(".rp-policy-report div[class*=slider]").length}`);
        // Changed as the slider does it; the shown value follows the saved one.
        api.trigger(GROUP, "setPolicySecond", livelihood, 40);
        await sleep(2500);
        const panel = document.querySelector(".rp-policy-report");
        const changed = panel ? panel.textContent || "" : "";
        check("A changed detail setting is shown as saved (40%)", squeeze(changed).indexOf(squeeze(label) + "40%") >= 0, changed.substring(0, 160));
        report("shot:detail-livelihood-40");
        await sleep(1500);
        api.trigger(GROUP, "setPolicySecond", livelihood, 32);
        await sleep(1000);
        api.trigger("game", "closeActivePanel");
        await sleep(1000);

        const months = translate("RealisticPolicies.UI.Second.Months", "").replace("{N}", "12");
        text = await showDetail(translate, "RealisticPolicies.RentSupport", "detail-rent");
        check("Rent support shows its detail setting in months (12)", text.indexOf(title) >= 0 && !!months && text.indexOf(months) >= 0, `"${months}" in: ${text.substring(0, 200)}`);
        api.trigger("game", "closeActivePanel");
        await sleep(1000);

        text = await showDetail(translate, "RealisticPolicies.Scholarships", "detail-scholarships");
        check("Scholarships show their detail setting as money (\u00a2300)", text.indexOf(title) >= 0 && text.indexOf("\u00a2300") >= 0, text.substring(0, 200));
        api.trigger("game", "closeActivePanel");
    } catch (e) {
        check("Detail settings UI test ran without errors", false, String(e && e.stack ? e.stack : e));
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
        } else if (stage === 3) {
            runReportTest(translate);
        } else if (stage === 4) {
            runProtestTest(translate);
        } else if (stage === 5) {
            runBuildingPanelTest(translate);
        } else if (stage === 6) {
            runActionTest(translate);
        } else if (stage === 7) {
            runDetailTest(translate);
        }
    }, [stage]);
    return null;
}

export default function register(moduleRegistry) {
    moduleRegistry.extend(POLICIES_PAGE_MODULE, "CityInfoPolicies", extendPoliciesPage);
    moduleRegistry.extend(CITY_POLICY_MODULE, "CityPolicy", extendCityPolicy);
    moduleRegistry.extend(BUDGET_SECTION_MODULE, "BudgetSection", extendBudgetSection);
    moduleRegistry.extend(CITY_INFO_FIELD_MODULE, "CityInfoField", extendCityInfoField);
    if (InfoSection && InfoRow) {
        moduleRegistry.extend(CITY_INFO_PANEL_MODULE, "CityInfoPanel", extendCityInfoPanel);
        moduleRegistry.extend(TABS_MODULE, "TabBar", extendTabBar);
        moduleRegistry.extend(TABS_MODULE, "TabNav", extendTabNav);
        moduleRegistry.extend(DETAIL_LAYOUT_MODULE, "DetailLayout", extendDetailLayout);
    } else {
        console.error("RealisticPolicies: info row components not found; the report panel is unavailable.");
    }
    moduleRegistry.append("Game", SelfTestRunner);
    if (InfoSection && InfoRow && Dropdown && DropdownToggle && DropdownItem) {
        moduleRegistry.extend(SECTIONS_MODULE, "selectedInfoSectionComponents", (components) => {
            components[WELFARE_SECTION_TYPE] = WelfareSeriesSection;
            components[PROTEST_SECTION_TYPE] = ProtestBuildingSection;
            return components;
        });
    } else {
        console.error("RealisticPolicies: info panel components not found; the welfare office option is unavailable.");
    }
}

export const hasCSS = false;
