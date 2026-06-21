from flux_llm_kb.glob_policy import effective_glob_policy


def test_effective_glob_policy_extends_global_defaults():
    root = {
        "include_globs": ["**/*.pdf"],
        "exclude_globs": ["client-private/**"],
        "glob_mode": "extend",
    }

    result = effective_glob_policy(
        root,
        global_include=["**/*.md"],
        global_exclude=["private/**", "node_modules/**"],
    )

    assert result["mode"] == "extend"
    assert result["include_globs"] == ["**/*.md", "**/*.pdf"]
    assert result["exclude_globs"] == ["private/**", "node_modules/**", "client-private/**"]


def test_effective_glob_policy_override_uses_only_root_values():
    root = {
        "include_globs": ["**/*.docx"],
        "exclude_globs": ["archive/**"],
        "glob_mode": "override",
    }

    result = effective_glob_policy(
        root,
        global_include=["**/*.md"],
        global_exclude=["private/**"],
    )

    assert result["include_globs"] == ["**/*.docx"]
    assert result["exclude_globs"] == ["archive/**"]
