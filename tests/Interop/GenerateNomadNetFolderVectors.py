"""Execute primary folder operations on synthetic owned trees; no UI/assets copied."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import subprocess
from types import SimpleNamespace

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def generate(output, observations, work):
    source, imports = reference()
    from nomadnet.ui.textui.Editor import PageEditorDisplay as Editor
    import urwid

    root = Path(work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    pages, files = root / "pages", root / "files"
    pages.mkdir()
    files.mkdir()
    statuses, prompts = [], []
    owner = SimpleNamespace(app=SimpleNamespace(pagespath=str(pages), filespath=str(files)),
                            current_path=None, expanded=set(), editor=urwid.Edit(multiline=True),
                            dirty=False, editable=True, is_exec=False, _loading=False,
                            close_dialog=lambda: None, refresh_tree=lambda: None,
                            save_state=lambda: None, update_title=lambda: None,
                            set_preview_note=lambda text: None, set_status=statuses.append)
    for name in ("_roots", "_is_root", "_confine", "_atomic_write", "_walk_pages", "_read_allowed", "_folder_permissions"):
        setattr(owner, name, lambda *args, _name=name, **kwargs: getattr(Editor, _name)(owner, *args, **kwargs))
    owner._confirm_overwrite = lambda path, proceed: prompts.append("overwrite " + Path(path).name)

    def relative(path):
        return None if path is None else Path(path).relative_to(root).as_posix()

    def snapshot():
        return [{"path": item.relative_to(root).as_posix(), "directory": item.is_dir(),
                 **({} if item.is_dir() else {"sha256": hashlib.sha256(item.read_bytes()).hexdigest()})}
                for item in sorted(root.rglob("*"))]

    Editor._do_new(owner, str(pages / "drafts" / "chapter") + os.sep)
    Editor._do_new(owner, str(pages / "drafts" / "empty") + os.sep)
    Editor._do_new(owner, str(pages / "drafts" / "chapter") + os.sep)
    content = ">Original synthetic notice\nA bounded sample.\n"
    owner._atomic_write(str(pages / "drafts" / "chapter" / "notice.mu"), content)
    owner._atomic_write(str(pages / "drafts" / "chapter" / "notice.mu.allowed"), "a" * 32 + "\n")
    (pages / "drafts" / "chapter" / "binary.dat").write_bytes(bytes([0, 255, 13, 10]))
    initial = snapshot()
    initial_files = {item["path"]: base64.b64encode((root / item["path"]).read_bytes()).decode("ascii")
                     for item in initial if not item["directory"]}
    owner.current_path = str(pages / "drafts" / "chapter" / "notice.mu")
    owner.editor.set_edit_text(content)
    owner.dirty = True
    Editor._do_rename(owner, str(pages / "drafts"), "renamed")
    renamed = {"tree": snapshot(), "selection": relative(owner.current_path), "dirty": owner.dirty}
    Editor._do_rename(owner, str(pages / "renamed"), str(files / "archive" / "moved"))
    moved = {"tree": snapshot(), "selection": relative(owner.current_path), "dirty": owner.dirty}
    (files / "occupied").mkdir()
    Editor._do_rename(owner, str(files / "archive" / "moved"), str(files / "occupied"))
    overwrite = {"prompts": list(prompts), "sourceRemains": (files / "archive" / "moved").is_dir()}
    owner._selected_tree = lambda: SimpleNamespace(path=str(pages))
    Editor.rename_dialog(owner)
    Editor.delete_dialog(owner)
    root_statuses = statuses[-2:]
    Editor._do_delete(owner, str(files / "archive" / "moved"))
    deleted = {"tree": snapshot(), "selection": relative(owner.current_path),
               "dirty": owner.dirty, "text": owner.editor.edit_text}

    # Separate observations inventory remaining bulk-permission rules, not native implementation.
    permissions = files / "permissions"
    (permissions / ".hidden").mkdir(parents=True)
    (permissions / ".hidden" / "skipped").write_text("hidden directory", encoding="utf-8")
    (permissions / "visible.txt").write_text("synthetic", encoding="utf-8")
    (permissions / ".visible-hidden-file").write_text("synthetic", encoding="utf-8")
    owner._atomic_write(str(permissions / "visible.txt.allowed"), "a" * 32 + "\n")
    walked = sorted(Path(path).relative_to(permissions).as_posix() for path in owner._walk_pages(str(permissions)))
    permission_state = list(owner._folder_permissions(str(permissions)))
    result = {"revision": REVISION,
              "method": "Actual PageEditorDisplay _do_new/_do_rename/_do_delete, root UI guards, _walk_pages/_folder_permissions and atomic writes on synthetic owned files; synthetic UI/state observer owner, actual urwid.Edit. Overwrite confirmation captured and cancelled, no rendered primary UI claim.",
              "source": "nomadnet/ui/textui/Editor.py",
              "sourceSha256": hashlib.sha256(subprocess.check_output(
                  ["git", "-C", str(source), "show", f"{REVISION}:nomadnet/ui/textui/Editor.py"])).hexdigest(),
              "notice": content, "initial": initial, "initialFiles": initial_files, "renamed": renamed, "moved": moved,
              "overwrite": overwrite, "rootStatuses": root_statuses, "deleted": deleted,
              "bulkPermissionsInventoryOnly": {"walked": walked, "state": permission_state},
              "statuses": statuses}
    Path(output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    Path(observations).write_text(json.dumps({"imports": imports, "work": str(root)}, indent=2) + "\n",
                                  encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    parser.add_argument("--work", required=True)
    args = parser.parse_args()
    generate(args.output, args.observations, args.work)
