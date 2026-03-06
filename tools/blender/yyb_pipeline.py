import argparse
import addon_utils
import json
import math
import struct
import shutil
import sys
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path

import bpy
from mathutils import Euler, Matrix, Quaternion, Vector


def parse_args() -> argparse.Namespace:
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1 :]
    else:
        argv = []

    parser = argparse.ArgumentParser(description="Inspect and export YYB Miku assets from Blender.")
    parser.add_argument(
        "command",
        choices=["inspect", "export", "export-fbx", "export-vrm"],
        help="Action to perform.",
    )
    parser.add_argument(
        "--output-dir",
        required=True,
        help="Directory for generated reports and exported assets.",
    )
    parser.add_argument(
        "--armature-name",
        default="",
        help="Optional explicit armature name. Defaults to the best armature in the scene.",
    )
    parser.add_argument(
        "--fbx-name",
        default="yyb-miku-export.fbx",
        help="Output FBX filename for FBX export modes.",
    )
    parser.add_argument(
        "--vrm-name",
        default="yyb-miku-export.vrm",
        help="Output VRM filename for VRM export mode.",
    )
    parser.add_argument(
        "--glb-name",
        default="yyb-miku-source.glb",
        help="Output filename for a copied source GLB fallback when one exists beside the blend.",
    )
    parser.add_argument(
        "--preview-name",
        default="yyb-miku-preview.png",
        help="Output preview filename for export modes.",
    )
    parser.add_argument(
        "--manifest-name",
        default="manifest.json",
        help="Output manifest filename for all commands.",
    )
    parser.add_argument(
        "--source-blend",
        default="",
        help="Original source blend path when running against a working copy.",
    )
    parser.add_argument(
        "--exclude-object",
        action="append",
        default=[],
        help="Optional mesh object name to exclude from export and validation. Repeat for multiple objects.",
    )
    return parser.parse_args(argv)


def ensure_dir(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True)
    return path


def default_vrm_meta() -> dict:
    return {
        "name": "YYB Hatsune Miku",
        "authors": ["YYB"],
        "version": "ver.3",
        "references": ["yyb-miku-ver-3-mikumikudance"],
        "copyrightInformation": "Original model by YYB",
    }


UNITY_TARGET_HEIGHT = 1.60
EXPORT_HEIGHT_TOLERANCE_RATIO = 0.15
TRANSFORM_EPSILON = 1e-5
GLTF_TRANSFORM_EPSILON = 1e-4
TEMP_DIR_CLEANUP_WARNINGS: list[dict[str, str | int | None]] = []
FACE_ALPHA_MODE_OVERRIDES = {
    "Material.004": "MASK",
    "Material.005": "MASK",
    "Material.010": "MASK",
}
FACE_ALPHA_CUTOFF = 0.5
SHADOW_OVERRIDE_MATERIALS = {
    "Material.020": {
        "color": (0.72, 0.64, 0.66, 1.0),
        "alpha_scale": 1.0,
        "alpha_mode": "BLEND",
    }
}
DEFAULT_EXPORT_EXCLUDED_OBJECT_NAMES: set[str] = {"01", "face00"}
EXPORT_EXCLUDED_OBJECT_NAMES: set[str] = set()
UNITY_HUMANOID_BONE_MAPPING = {
    "hips": "\u8170",
    "spine": "\u4e0a\u534a\u8eab",
    "chest": "\u4e0a\u534a\u8eab2",
    "neck": "\u9996",
    "head": "\u982d",
    "jaw": "\u3042\u3054",
    "left_eye": "\u5de6\u76ee",
    "right_eye": "\u53f3\u76ee",
    "left_shoulder": "\u5de6\u80a9",
    "left_upper_arm": "\u5de6\u8155",
    "left_lower_arm": "\u5de6\u3072\u3058",
    "left_hand": "\u5de6\u624b\u9996",
    "right_shoulder": "\u53f3\u80a9",
    "right_upper_arm": "\u53f3\u8155",
    "right_lower_arm": "\u53f3\u3072\u3058",
    "right_hand": "\u53f3\u624b\u9996",
    "left_upper_leg": "\u5de6\u8db3",
    "left_lower_leg": "\u5de6\u3072\u3056",
    "left_foot": "\u5de6\u8db3\u9996",
    "left_toes": "\u5de6\u3064\u307e\u5148",
    "right_upper_leg": "\u53f3\u8db3",
    "right_lower_leg": "\u53f3\u3072\u3056",
    "right_foot": "\u53f3\u8db3\u9996",
    "right_toes": "\u53f3\u3064\u307e\u5148",
}


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def rounded_list(values, digits: int = 6) -> list[float]:
    return [round(float(value), digits) for value in values]


def install_lenient_tempdir_cleanup(temp_root: Path | None = None):
    original = tempfile.TemporaryDirectory

    class LenientTemporaryDirectory:
        def __init__(self, *args, **kwargs) -> None:
            del args, kwargs
            root = (temp_root or Path(tempfile.gettempdir())).resolve()
            root.mkdir(parents=True, exist_ok=True)
            self.path = root / f"tmp-{uuid.uuid4().hex}"
            self.path.mkdir(parents=False, exist_ok=False)
            self.name = str(self.path)

        def __enter__(self) -> str:
            return self.name

        def __exit__(self, exc_type, exc, traceback) -> bool:
            self.cleanup()
            return False

        def cleanup(self) -> None:
            try:
                shutil.rmtree(self.name)
            except OSError as exc:
                if isinstance(exc, PermissionError) or getattr(exc, "winerror", None) == 5:
                    TEMP_DIR_CLEANUP_WARNINGS.append(
                        {
                            "path": getattr(self, "name", None),
                            "error": str(exc),
                            "winerror": getattr(exc, "winerror", None),
                        }
                    )
                    return
                raise

    tempfile.TemporaryDirectory = LenientTemporaryDirectory
    return original


def restore_tempdir_cleanup(original) -> None:
    tempfile.TemporaryDirectory = original


def consume_tempdir_cleanup_warnings() -> list[dict[str, str | int | None]]:
    warnings = list(TEMP_DIR_CLEANUP_WARNINGS)
    TEMP_DIR_CLEANUP_WARNINGS.clear()
    return warnings


def describe_bounds(min_v: Vector, max_v: Vector) -> dict:
    size = max_v - min_v
    center = (min_v + max_v) / 2.0
    return {
        "min": rounded_list(min_v),
        "max": rounded_list(max_v),
        "size": rounded_list(size),
        "center": rounded_list(center),
    }


def vector_is_close(values, target: tuple[float, ...], epsilon: float = TRANSFORM_EPSILON) -> bool:
    return all(abs(float(value) - expected) <= epsilon for value, expected in zip(values, target))


def quaternion_is_identity_wxyz(values, epsilon: float = TRANSFORM_EPSILON) -> bool:
    quaternion = [float(value) for value in values]
    return (
        vector_is_close(quaternion[1:], (0.0, 0.0, 0.0), epsilon)
        and abs(abs(quaternion[0]) - 1.0) <= epsilon
    )


def quaternion_is_identity_xyzw(values, epsilon: float = GLTF_TRANSFORM_EPSILON) -> bool:
    quaternion = [float(value) for value in values]
    return (
        vector_is_close(quaternion[:3], (0.0, 0.0, 0.0), epsilon)
        and abs(abs(quaternion[3]) - 1.0) <= epsilon
    )


def object_transform_snapshot(obj: bpy.types.Object) -> dict:
    return {
        "location": rounded_list(obj.location),
        "rotationQuaternion": rounded_list(obj.rotation_quaternion),
        "scale": rounded_list(obj.scale),
    }


def object_transform_is_identity(obj: bpy.types.Object, epsilon: float = TRANSFORM_EPSILON) -> bool:
    return (
        vector_is_close(obj.location, (0.0, 0.0, 0.0), epsilon)
        and quaternion_is_identity_wxyz(obj.rotation_quaternion, epsilon)
        and vector_is_close(obj.scale, (1.0, 1.0, 1.0), epsilon)
    )


def bone_property_to_export_name(name: str) -> str:
    parts = name.split("_")
    return parts[0] + "".join(part.capitalize() for part in parts[1:])


def load_glb_structure(path: Path) -> tuple[int, list[tuple[bytes, bytes]], int, dict]:
    data = path.read_bytes()
    if len(data) < 20:
        raise RuntimeError(f"GLB file '{path}' is too small to inspect.")

    magic, version, total_length = struct.unpack_from("<4sII", data, 0)
    if magic != b"glTF":
        raise RuntimeError(f"File '{path}' is not a GLB/VRM container.")
    if total_length != len(data):
        raise RuntimeError(
            f"File '{path}' has an invalid GLB length header ({total_length} != {len(data)})."
        )

    offset = 12
    chunks: list[tuple[bytes, bytes]] = []
    while offset < len(data):
        chunk_length, chunk_type = struct.unpack_from("<I4s", data, offset)
        offset += 8
        chunk_data = data[offset : offset + chunk_length]
        offset += chunk_length
        chunks.append((chunk_type, chunk_data))

    json_index = next((index for index, chunk in enumerate(chunks) if chunk[0] == b"JSON"), None)
    if json_index is None:
        raise RuntimeError(f"GLB file '{path}' does not contain a JSON chunk.")

    json_obj = json.loads(chunks[json_index][1].decode("utf-8"))
    return version, chunks, json_index, json_obj


def write_glb_structure(
    path: Path,
    version: int,
    chunks: list[tuple[bytes, bytes]],
    json_index: int,
    json_obj: dict,
) -> None:
    json_bytes = json.dumps(json_obj, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    json_padding = (4 - (len(json_bytes) % 4)) % 4
    json_bytes += b" " * json_padding
    chunks[json_index] = (b"JSON", json_bytes)

    rebuilt = bytearray()
    rebuilt.extend(struct.pack("<4sII", b"glTF", version, 0))
    for chunk_type, chunk_data in chunks:
        rebuilt.extend(struct.pack("<I4s", len(chunk_data), chunk_type))
        rebuilt.extend(chunk_data)
    struct.pack_into("<I", rebuilt, 8, len(rebuilt))
    path.write_bytes(rebuilt)


def find_source_glb_path() -> Path | None:
    blend_dir = Path(bpy.data.filepath).resolve().parent
    candidates = sorted(blend_dir.glob("*.glb"))
    if not candidates:
        return None
    return candidates[0]


def gltf_rotation_to_blender(rotation: list[float]) -> Quaternion:
    return Quaternion((rotation[3], rotation[0], rotation[1], rotation[2]))


def blender_rotation_to_gltf(rotation: Quaternion) -> list[float]:
    return [rotation.x, rotation.y, rotation.z, rotation.w]


def gltf_matrix_to_blender(matrix_values: list[float]) -> Matrix:
    return Matrix(
        (
            matrix_values[0:4],
            matrix_values[4:8],
            matrix_values[8:12],
            matrix_values[12:16],
        )
    ).transposed()


def node_transform_to_matrix(node: dict) -> Matrix:
    matrix_values = node.get("matrix")
    if isinstance(matrix_values, list) and len(matrix_values) == 16:
        return gltf_matrix_to_blender([float(value) for value in matrix_values])

    translation = Vector(node.get("translation", [0.0, 0.0, 0.0]))
    rotation = gltf_rotation_to_blender(node.get("rotation", [0.0, 0.0, 0.0, 1.0]))
    scale = Vector(node.get("scale", [1.0, 1.0, 1.0]))
    return Matrix.LocRotScale(translation, rotation, scale)


def set_node_transform_from_matrix(node: dict, matrix: Matrix) -> None:
    location, rotation, scale = matrix.decompose()
    translation = rounded_list(location)
    rotation_xyzw = rounded_list(blender_rotation_to_gltf(rotation))
    scale_values = rounded_list(scale)

    node.pop("matrix", None)
    if vector_is_close(translation, (0.0, 0.0, 0.0), GLTF_TRANSFORM_EPSILON):
        node.pop("translation", None)
    else:
        node["translation"] = translation

    if quaternion_is_identity_xyzw(rotation_xyzw, GLTF_TRANSFORM_EPSILON):
        node.pop("rotation", None)
    else:
        node["rotation"] = rotation_xyzw

    if vector_is_close(scale_values, (1.0, 1.0, 1.0), GLTF_TRANSFORM_EPSILON):
        node.pop("scale", None)
    else:
        node["scale"] = scale_values


def ensure_object_mode(active_object: bpy.types.Object | None = None) -> None:
    if active_object is not None:
        bpy.context.view_layer.objects.active = active_object
    current = bpy.context.object
    if current is not None and current.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")


def is_descendant_of(obj: bpy.types.Object, root: bpy.types.Object) -> bool:
    current = obj.parent
    while current is not None:
        if current == root:
            return True
        current = current.parent
    return False


def is_export_excluded_object(obj: bpy.types.Object) -> bool:
    return obj.name in EXPORT_EXCLUDED_OBJECT_NAMES


def armature_descendant_meshes(armature: bpy.types.Object) -> list[bpy.types.Object]:
    return [
        obj
        for obj in bpy.data.objects
        if obj.type == "MESH"
        and not is_export_excluded_object(obj)
        and (obj.parent == armature or is_descendant_of(obj, armature))
    ]


def choose_armature(explicit_name: str) -> bpy.types.Object:
    armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
    if not armatures:
        raise RuntimeError("No armatures found in the scene.")

    if explicit_name:
        for armature in armatures:
            if armature.name == explicit_name:
                return armature
        raise RuntimeError(f"Armature '{explicit_name}' was not found.")

    return max(armatures, key=lambda arm: (len(armature_descendant_meshes(arm)), len(arm.data.bones)))


def object_bounds_world(objects: list[bpy.types.Object]) -> tuple[Vector, Vector]:
    min_v = Vector((math.inf, math.inf, math.inf))
    max_v = Vector((-math.inf, -math.inf, -math.inf))

    for obj in objects:
        if obj.type != "MESH":
            continue
        for corner in obj.bound_box:
            world = obj.matrix_world @ Vector(corner)
            min_v.x = min(min_v.x, world.x)
            min_v.y = min(min_v.y, world.y)
            min_v.z = min(min_v.z, world.z)
            max_v.x = max(max_v.x, world.x)
            max_v.y = max(max_v.y, world.y)
            max_v.z = max(max_v.z, world.z)

    if not math.isfinite(min_v.x):
        raise RuntimeError("Unable to compute mesh bounds.")

    return min_v, max_v


def bounds_for_objects(objects: list[bpy.types.Object]) -> tuple[Vector, Vector, dict]:
    min_v, max_v = object_bounds_world(objects)
    return min_v, max_v, describe_bounds(min_v, max_v)


def bone_parent_chain(armature: bpy.types.Object, bone_name: str) -> list[str]:
    armature_data = armature.data
    if not isinstance(armature_data, bpy.types.Armature):
        return []

    bone = armature_data.bones.get(bone_name)
    chain = []
    while bone is not None:
        chain.append(bone.name)
        bone = bone.parent
    return chain


def validate_humanoid_ancestry(armature: bpy.types.Object, mapped_human_bones: dict[str, str]) -> dict:
    checks = []

    def add_check(ancestor_key: str, descendant_key: str) -> None:
        ancestor_bone = mapped_human_bones.get(ancestor_key)
        descendant_bone = mapped_human_bones.get(descendant_key)
        chain = bone_parent_chain(armature, descendant_bone) if descendant_bone else []
        passed = bool(ancestor_bone and descendant_bone and ancestor_bone in chain[1:])
        checks.append(
            {
                "ancestorHumanBone": ancestor_key,
                "ancestorBone": ancestor_bone,
                "descendantHumanBone": descendant_key,
                "descendantBone": descendant_bone,
                "descendantChainToRoot": chain,
                "passed": passed,
            }
        )

    add_check("hips", "spine")
    add_check("hips", "left_upper_leg")
    add_check("hips", "right_upper_leg")
    add_check("spine", "chest")
    add_check("chest", "neck")
    add_check("neck", "head")
    add_check("left_upper_arm", "left_lower_arm")
    add_check("left_lower_arm", "left_hand")
    add_check("right_upper_arm", "right_lower_arm")
    add_check("right_lower_arm", "right_hand")
    add_check("left_upper_leg", "left_lower_leg")
    add_check("left_lower_leg", "left_foot")
    add_check("right_upper_leg", "right_lower_leg")
    add_check("right_lower_leg", "right_foot")

    failures = [
        (
            f"{check['ancestorHumanBone']}({check['ancestorBone']}) must be an ancestor of "
            f"{check['descendantHumanBone']}({check['descendantBone']}); "
            f"chain={check['descendantChainToRoot']}"
        )
        for check in checks
        if not check["passed"]
    ]

    return {
        "passed": not failures,
        "checks": checks,
        "errors": failures,
    }


def unresolved_images() -> list[dict[str, str]]:
    missing = []
    for image in bpy.data.images:
        filepath = bpy.path.abspath(image.filepath) if image.filepath else ""
        if filepath and not Path(filepath).exists():
            missing.append({"name": image.name, "filepath": filepath})
    return missing


def detect_vrm_support() -> dict:
    enabled = set(bpy.context.preferences.addons.keys())
    candidates = []

    for module in addon_utils.modules():
        module_name = getattr(module, "__name__", "")
        bl_info = getattr(module, "bl_info", {}) or {}
        display_name = bl_info.get("name", "")
        if "vrm" not in module_name.lower() and "vrm" not in display_name.lower():
            continue

        version = bl_info.get("version", ())
        candidates.append(
            {
                "module": module_name,
                "displayName": display_name or None,
                "version": list(version) if version else [],
                "enabled": module_name in enabled,
                "path": getattr(module, "__file__", None),
            }
        )

    return {
        "operatorAvailable": "vrm" in dir(bpy.ops.export_scene),
        "addonCandidates": sorted(candidates, key=lambda item: item["module"]),
    }


def ensure_vrm_support() -> dict:
    support = detect_vrm_support()
    if support["operatorAvailable"] and any(
        candidate["enabled"] for candidate in support["addonCandidates"]
    ):
        return support

    enable_errors = []
    for candidate in support["addonCandidates"]:
        if candidate["enabled"]:
            continue

        try:
            addon_utils.enable(candidate["module"], default_set=False, persistent=False)
            if candidate["module"] not in bpy.context.preferences.addons:
                bpy.ops.preferences.addon_enable(module=candidate["module"])
        except Exception as exc:  # pragma: no cover - Blender addon internals vary by version
            enable_errors.append({"module": candidate["module"], "error": str(exc)})

    updated = detect_vrm_support()
    if enable_errors:
        updated["enableErrors"] = enable_errors
    return updated


def relink_missing_images() -> list[dict[str, str]]:
    blend_dir = Path(bpy.data.filepath).resolve().parent
    file_index: dict[str, Path] = {}

    for path in blend_dir.rglob("*"):
        if path.is_file():
            file_index.setdefault(path.name.lower(), path)

    relinked = []
    for image in bpy.data.images:
        filepath = bpy.path.abspath(image.filepath) if image.filepath else ""
        if filepath and Path(filepath).exists():
            continue

        candidate_names = []
        if filepath:
            candidate_names.append(Path(filepath).name.lower())
        candidate_names.append(Path(image.name).name.lower())

        candidate = None
        for name in candidate_names:
            if name in file_index:
                candidate = file_index[name]
                break

        if candidate is None:
            continue

        image.filepath = str(candidate)
        image.filepath_raw = str(candidate)
        try:
            image.reload()
        except RuntimeError:
            pass

        relinked.append({"image": image.name, "path": str(candidate)})

    return relinked


def scene_summary(armature: bpy.types.Object, relinked_images: list[dict[str, str]] | None = None) -> dict:
    meshes = armature_descendant_meshes(armature)
    min_v, max_v = object_bounds_world(meshes)
    size = max_v - min_v
    center = (min_v + max_v) / 2.0
    total_shape_keys = sum(len(obj.data.shape_keys.key_blocks) if obj.data.shape_keys else 0 for obj in meshes)

    return {
        "blendFile": bpy.data.filepath,
        "armature": {
            "name": armature.name,
            "boneCount": len(armature.data.bones),
        },
        "meshCount": len(meshes),
        "actionCount": len(bpy.data.actions),
        "totalShapeKeyCount": total_shape_keys,
        "meshes": [
            {
                "name": obj.name,
                "parent": obj.parent.name if obj.parent else None,
                "vertexGroupCount": len(obj.vertex_groups),
                "materialCount": len(obj.data.materials),
                "shapeKeyCount": len(obj.data.shape_keys.key_blocks) if obj.data.shape_keys else 0,
                "armatureModifiers": [
                    modifier.object.name if getattr(modifier, "object", None) else None
                    for modifier in obj.modifiers
                    if modifier.type == "ARMATURE"
                ],
            }
            for obj in meshes
        ],
        "actions": [action.name for action in bpy.data.actions],
        "relinkedImages": relinked_images or [],
        "missingImages": unresolved_images(),
        "bounds": {
            "min": [round(value, 6) for value in min_v],
            "max": [round(value, 6) for value in max_v],
            "size": [round(value, 6) for value in size],
            "center": [round(value, 6) for value in center],
        },
    }


def write_json(path: Path, payload: dict) -> None:
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def path_or_none(path: Path | None) -> str | None:
    return str(path) if path else None


def build_manifest(
    *,
    command: str,
    summary: dict,
    inspect_path: Path,
    source_blend: str,
    status: str,
    manifest_path: Path,
    preview_path: Path | None = None,
    fbx_path: Path | None = None,
    vrm_path: Path | None = None,
    glb_path: Path | None = None,
    vrm_support: dict | None = None,
    export_details: dict | None = None,
    validation: dict | None = None,
    error: str | None = None,
) -> dict:
    warnings = []
    if summary["totalShapeKeyCount"] == 0:
        warnings.append("No shape keys were detected; facial expressions and lip sync are not validated in this phase.")
    if summary["actionCount"] == 0:
        warnings.append("No Blender actions were detected; animation export is out of scope for this phase.")

    return {
        "generatedAt": now_iso(),
        "command": command,
        "status": status,
        "sourceBlend": source_blend or summary["blendFile"],
        "workingBlend": summary["blendFile"],
        "manifest": str(manifest_path),
        "artifacts": {
            "inspect": str(inspect_path),
            "preview": path_or_none(preview_path),
            "fbx": path_or_none(fbx_path),
            "vrm": path_or_none(vrm_path),
            "glbFallback": path_or_none(glb_path),
        },
        "scene": {
            "armature": summary["armature"],
            "meshCount": summary["meshCount"],
            "actionCount": summary["actionCount"],
            "totalShapeKeyCount": summary["totalShapeKeyCount"],
            "missingImageCount": len(summary["missingImages"]),
            "relinkedImageCount": len(summary["relinkedImages"]),
            "bounds": summary["bounds"],
        },
        "vrmSupport": vrm_support,
        "exportDetails": export_details or {},
        "validation": validation or {},
        "exportExclusions": sorted(EXPORT_EXCLUDED_OBJECT_NAMES),
        "warnings": warnings,
        "error": error,
    }


def deselect_all() -> None:
    for obj in bpy.context.selected_objects:
        obj.select_set(False)


def select_export_objects(armature: bpy.types.Object) -> list[bpy.types.Object]:
    export_objects = [armature, *armature_descendant_meshes(armature)]
    deselect_all()
    for obj in export_objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = armature
    return export_objects


def apply_object_transforms(
    objects: list[bpy.types.Object],
    *,
    location: bool = False,
    rotation: bool = False,
    scale: bool = False,
) -> int:
    if not objects:
        return 0

    applied = 0
    for obj in objects:
        ensure_object_mode(obj)
        deselect_all()
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.transform_apply(location=location, rotation=rotation, scale=scale)
        applied += 1

    return applied


def apply_armature_root_transforms(
    armature: bpy.types.Object,
    *,
    location: bool = False,
    rotation: bool = False,
    scale: bool = False,
) -> int:
    return apply_object_transforms(
        [armature],
        location=location,
        rotation=rotation,
        scale=scale,
    )


def apply_mesh_object_transforms(
    armature: bpy.types.Object,
    *,
    location: bool = False,
    rotation: bool = False,
    scale: bool = False,
) -> int:
    return apply_object_transforms(
        armature_descendant_meshes(armature),
        location=location,
        rotation=rotation,
        scale=scale,
    )


def validate_mesh_skinning(summary: dict) -> dict:
    issues = []
    for mesh in summary["meshes"]:
        armature_modifiers = [name for name in mesh["armatureModifiers"] if name]
        if not armature_modifiers:
            issues.append(
                {
                    "mesh": mesh["name"],
                    "reason": "missingArmatureModifier",
                }
            )

        if mesh["vertexGroupCount"] <= 0:
            issues.append(
                {
                    "mesh": mesh["name"],
                    "reason": "missingVertexGroups",
                }
            )

    return {
        "passed": not issues,
        "issueCount": len(issues),
        "issues": issues,
    }


def extract_material_surface_inputs(
    material: bpy.types.Material,
) -> tuple[bpy.types.Image | None, bpy.types.Image | None, tuple[float, float, float, float]]:
    fallback_color = tuple(float(value) for value in material.diffuse_color)
    if not material.use_nodes or material.node_tree is None:
        return None, None, fallback_color

    color_image = None
    alpha_image = None
    first_image = None
    principled_node = None
    for node in material.node_tree.nodes:
        if first_image is None and node.bl_idname == "ShaderNodeTexImage" and getattr(node, "image", None):
            first_image = node.image
        if principled_node is None and node.bl_idname == "ShaderNodeBsdfPrincipled":
            principled_node = node

    if principled_node is not None:
        fallback_color = tuple(float(value) for value in principled_node.inputs["Base Color"].default_value)
        if principled_node.inputs["Base Color"].is_linked:
            source_node = principled_node.inputs["Base Color"].links[0].from_node
            if source_node.bl_idname == "ShaderNodeTexImage" and getattr(source_node, "image", None):
                color_image = source_node.image
        if principled_node.inputs["Alpha"].is_linked:
            source_node = principled_node.inputs["Alpha"].links[0].from_node
            if source_node.bl_idname == "ShaderNodeTexImage" and getattr(source_node, "image", None):
                alpha_image = source_node.image

    if color_image is None:
        color_image = first_image
    if alpha_image is None:
        alpha_image = color_image

    return color_image, alpha_image, fallback_color


def ensure_shadow_override_image(
    material: bpy.types.Material,
    alpha_image: bpy.types.Image | None,
    shadow_color: tuple[float, float, float, float],
    alpha_scale: float,
) -> bpy.types.Image | None:
    if alpha_image is None:
        return None

    generated_dir = ensure_dir(Path(bpy.data.filepath).resolve().parent / ".generated-textures")
    output_path = generated_dir / f"{material.name}-shadow-override.png"
    image_name = f"{material.name}_shadow_override"

    width, height = alpha_image.size
    source_pixels = list(alpha_image.pixels[:])
    generated_pixels: list[float] = []
    for pixel_index in range(0, len(source_pixels), 4):
        alpha = max(0.0, min(1.0, source_pixels[pixel_index + 3] * alpha_scale))
        generated_pixels.extend(
            [
                float(shadow_color[0]),
                float(shadow_color[1]),
                float(shadow_color[2]),
                alpha,
            ]
        )

    existing = bpy.data.images.get(image_name)
    if existing is not None and list(existing.size) != [width, height]:
        bpy.data.images.remove(existing)
        existing = None

    image = existing or bpy.data.images.new(image_name, width=width, height=height, alpha=True)
    image.colorspace_settings.name = "sRGB"
    image.alpha_mode = "STRAIGHT"
    image.file_format = "PNG"
    image.filepath_raw = str(output_path)
    image.pixels[:] = generated_pixels
    image.save()
    image.reload()
    return image


def convert_material_to_anime_principled(material: bpy.types.Material, source_metadata: dict[str, object] | None = None) -> None:
    color_image, alpha_image, fallback_color = extract_material_surface_inputs(material)
    alpha_mode = str((source_metadata or {}).get("alphaMode", "OPAQUE")).upper()
    alpha_mode = FACE_ALPHA_MODE_OVERRIDES.get(material.name, alpha_mode)
    double_sided = bool((source_metadata or {}).get("doubleSided", False))

    shadow_override = SHADOW_OVERRIDE_MATERIALS.get(material.name)
    if shadow_override:
        color_image = ensure_shadow_override_image(
            material,
            alpha_image or color_image,
            shadow_override["color"],
            float(shadow_override["alpha_scale"]),
        )
        alpha_image = color_image
        alpha_mode = str(shadow_override["alpha_mode"]).upper()
        fallback_color = shadow_override["color"]
        double_sided = True

    material.use_nodes = True
    node_tree = material.node_tree
    if node_tree is None:
        return

    for node in list(node_tree.nodes):
        node_tree.nodes.remove(node)

    output_node = node_tree.nodes.new("ShaderNodeOutputMaterial")
    output_node.location = (400.0, 0.0)

    shader_node = node_tree.nodes.new("ShaderNodeBsdfPrincipled")
    shader_node.location = (80.0, 0.0)
    shader_node.inputs["Base Color"].default_value = fallback_color
    shader_node.inputs["Metallic"].default_value = 0.0
    shader_node.inputs["Roughness"].default_value = 1.0
    if "Specular IOR Level" in shader_node.inputs:
        shader_node.inputs["Specular IOR Level"].default_value = 0.0

    node_tree.links.new(shader_node.outputs["BSDF"], output_node.inputs["Surface"])

    if color_image is not None:
        color_texture_node = node_tree.nodes.new("ShaderNodeTexImage")
        color_texture_node.location = (-320.0, 80.0)
        color_texture_node.image = color_image
        node_tree.links.new(color_texture_node.outputs["Color"], shader_node.inputs["Base Color"])

        if alpha_mode in {"BLEND", "MASK"}:
            alpha_source_node = color_texture_node
            if alpha_image is not None and alpha_image != color_image:
                alpha_texture_node = node_tree.nodes.new("ShaderNodeTexImage")
                alpha_texture_node.location = (-320.0, -120.0)
                alpha_texture_node.image = alpha_image
                alpha_source_node = alpha_texture_node

            node_tree.links.new(alpha_source_node.outputs["Alpha"], shader_node.inputs["Alpha"])
            material.blend_method = "CLIP" if alpha_mode == "MASK" else "BLEND"
            if hasattr(material, "alpha_threshold") and alpha_mode == "MASK":
                material.alpha_threshold = 0.5
            if hasattr(material, "shadow_method"):
                material.shadow_method = "HASHED"
        else:
            material.blend_method = "OPAQUE"
    else:
        material.blend_method = "OPAQUE"

    material.use_backface_culling = not double_sided


def normalize_materials_for_anime_vrm(material_metadata: dict[str, dict[str, object]] | None = None) -> list[str]:
    converted = []
    metadata = material_metadata or {}
    for material in bpy.data.materials:
        if material is None:
            continue
        convert_material_to_anime_principled(material, metadata.get(material.name))
        converted.append(material.name)
    return converted


def ensure_preview_camera(min_v: Vector, max_v: Vector) -> bpy.types.Object:
    center = (min_v + max_v) / 2.0
    size = max_v - min_v
    radius = max(size.x, size.y, size.z, 0.001)

    camera_data = bpy.data.cameras.new("YYBPreviewCamera")
    camera = bpy.data.objects.new("YYBPreviewCamera", camera_data)
    bpy.context.scene.collection.objects.link(camera)

    distance = radius * 2.25
    camera.location = Vector((center.x, center.y - distance, center.z + radius * 0.15))
    camera.rotation_euler = Euler((math.radians(82.0), 0.0, 0.0), "XYZ")
    camera.data.lens = 55
    camera.data.clip_start = 0.01
    camera.data.clip_end = max(1000.0, distance * 4.0)
    return camera


def render_preview(path: Path, meshes: list[bpy.types.Object]) -> None:
    scene = bpy.context.scene
    min_v, max_v = object_bounds_world(meshes)

    previous_camera = scene.camera
    previous_engine = scene.render.engine
    previous_filepath = scene.render.filepath
    previous_resolution_x = scene.render.resolution_x
    previous_resolution_y = scene.render.resolution_y
    previous_image_settings = scene.render.image_settings.file_format

    camera = ensure_preview_camera(min_v, max_v)
    camera_data = camera.data
    scene.camera = camera
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = 1024
    scene.render.resolution_y = 1024
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = str(path)

    try:
        bpy.ops.render.render(write_still=True)
    finally:
        scene.camera = previous_camera
        scene.render.engine = previous_engine
        scene.render.filepath = previous_filepath
        scene.render.resolution_x = previous_resolution_x
        scene.render.resolution_y = previous_resolution_y
        scene.render.image_settings.file_format = previous_image_settings
        bpy.data.objects.remove(camera, do_unlink=True)
        bpy.data.cameras.remove(camera_data, do_unlink=True)


def export_fbx(path: Path, armature: bpy.types.Object) -> None:
    select_export_objects(armature)
    bpy.ops.export_scene.fbx(
        filepath=str(path),
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        path_mode="COPY",
        embed_textures=False,
        add_leaf_bones=False,
    )


def prepare_unity_export_profile(armature: bpy.types.Object) -> dict:
    ensure_object_mode(armature)
    pre_transform = object_transform_snapshot(armature)

    initial_armature_bake_count = apply_armature_root_transforms(
        armature,
        location=True,
        rotation=True,
        scale=True,
    )
    initial_mesh_bake_count = apply_mesh_object_transforms(
        armature,
        location=True,
        rotation=True,
        scale=True,
    )
    initial_object_count = initial_armature_bake_count + initial_mesh_bake_count

    meshes = armature_descendant_meshes(armature)
    min_v, max_v, pre_bounds = bounds_for_objects(meshes)
    source_height = max_v.z - min_v.z
    if source_height <= TRANSFORM_EPSILON:
        raise RuntimeError("Unable to normalize export profile because the mesh height is zero.")

    scale_factor = UNITY_TARGET_HEIGHT / float(source_height)
    scale_bake_count = 0
    if abs(scale_factor - 1.0) > TRANSFORM_EPSILON:
        armature.scale = armature.scale * scale_factor
        scale_bake_count = apply_armature_root_transforms(armature, scale=True)

    meshes = armature_descendant_meshes(armature)
    scaled_min_v, scaled_max_v, scaled_bounds = bounds_for_objects(meshes)
    scaled_center = (scaled_min_v + scaled_max_v) / 2.0
    ground_offset = Vector((-scaled_center.x, -scaled_center.y, -scaled_min_v.z))
    location_bake_count = 0
    if not vector_is_close(ground_offset, (0.0, 0.0, 0.0), TRANSFORM_EPSILON):
        armature.location = armature.location + ground_offset
        location_bake_count = apply_armature_root_transforms(armature, location=True)

    meshes = armature_descendant_meshes(armature)
    final_min_v, final_max_v, final_bounds = bounds_for_objects(meshes)
    final_center = (final_min_v + final_max_v) / 2.0
    final_height = float(final_bounds["size"][2])
    height_tolerance = UNITY_TARGET_HEIGHT * EXPORT_HEIGHT_TOLERANCE_RATIO

    return {
        "targetHeight": round(UNITY_TARGET_HEIGHT, 6),
        "sourceHeight": round(float(source_height), 6),
        "appliedScaleFactor": round(float(scale_factor), 6),
        "mappedHumanoidBones": {
            bone_property_to_export_name(human_bone): bone_name
            for human_bone, bone_name in UNITY_HUMANOID_BONE_MAPPING.items()
        },
        "rootNormalization": {
            "initialTransformBake": {
                "location": True,
                "rotation": True,
                "scale": True,
                "objectCount": initial_object_count,
                "armatureObjectCount": initial_armature_bake_count,
                "meshObjectCount": initial_mesh_bake_count,
            },
            "scaleBakeObjectCount": scale_bake_count,
            "locationBakeObjectCount": location_bake_count,
            "preTransform": pre_transform,
            "postTransform": object_transform_snapshot(armature),
            "appliedGroundOffset": rounded_list(ground_offset),
            "boundsBeforeNormalization": pre_bounds,
            "boundsAfterScale": scaled_bounds,
            "finalBounds": final_bounds,
            "finalHeight": round(final_height, 6),
            "heightTolerance": round(float(height_tolerance), 6),
            "heightWithinTolerance": abs(final_height - UNITY_TARGET_HEIGHT) <= height_tolerance,
            "rootObjectIsIdentity": object_transform_is_identity(armature),
            "groundedToZero": abs(final_min_v.z) <= GLTF_TRANSFORM_EPSILON,
            "centeredOnOrigin": abs(final_center.x) <= GLTF_TRANSFORM_EPSILON
            and abs(final_center.y) <= GLTF_TRANSFORM_EPSILON,
        },
    }


def build_export_validation(summary: dict, export_details: dict) -> dict:
    skinning = validate_mesh_skinning(summary)
    unity_profile = export_details["unityProfile"]
    root_normalization = unity_profile["rootNormalization"]
    exported_scene_root = unity_profile.get("exportedSceneRoot", {})
    target_height = float(unity_profile["targetHeight"])
    final_height = float(root_normalization["finalHeight"])
    height_tolerance = float(root_normalization["heightTolerance"])

    validation = {
        "scaleTarget": round(target_height, 6),
        "finalHeight": round(final_height, 6),
        "heightTolerance": round(height_tolerance, 6),
        "heightWithinTolerance": abs(final_height - target_height) <= height_tolerance,
        "meshSkinningPassed": skinning["passed"],
        "meshSkinningIssues": skinning["issues"],
        "rootObjectIsIdentity": bool(root_normalization["rootObjectIsIdentity"]),
        "exportedSceneRootIsIdentity": bool(exported_scene_root.get("isIdentity", False)),
        "materialMode": export_details.get("materialMode"),
        "materialCount": export_details.get("materialCount"),
    }
    return validation


def enforce_export_validation(validation: dict, export_details: dict) -> None:
    errors = []
    if not validation["exportedSceneRootIsIdentity"]:
        errors.append("exported scene root is not identity after normalization")
    if not validation["heightWithinTolerance"]:
        errors.append(
            "final height "
            + f"{validation['finalHeight']} is outside target {validation['scaleTarget']} "
            + f"+/- {validation['heightTolerance']}"
        )
    if not validation["meshSkinningPassed"]:
        skinning_errors = ", ".join(
            f"{issue['mesh']}:{issue['reason']}" for issue in validation["meshSkinningIssues"]
        )
        errors.append(f"mesh skinning validation failed: {skinning_errors}")

    if errors:
        raise RuntimeError("Export validation failed: " + "; ".join(errors))


def configure_vrm_humanoid(armature: bpy.types.Object) -> dict:
    from io_scene_vrm.editor.extension import get_armature_extension
    from io_scene_vrm.editor.vrm1.property_group import Vrm1HumanBonesPropertyGroup

    addon_utils.enable("io_scene_vrm", default_set=False, persistent=False)
    if "io_scene_vrm" not in bpy.context.preferences.addons:
        bpy.ops.preferences.addon_enable(module="io_scene_vrm")

    armature_data = armature.data
    if not isinstance(armature_data, bpy.types.Armature):
        raise RuntimeError(f"{armature.name} does not reference an Armature datablock.")

    ext = get_armature_extension(armature_data)
    ext.spec_version = ext.SPEC_VERSION_VRM1

    Vrm1HumanBonesPropertyGroup.fixup_human_bones(armature)
    Vrm1HumanBonesPropertyGroup.update_all_bone_name_candidates(
        bpy.context, armature_data.name, force=True
    )

    human_bones = ext.vrm1.humanoid.human_bones
    manual_mapping = dict(UNITY_HUMANOID_BONE_MAPPING)

    applied = []
    skipped = []
    mapped_human_bones = {}
    for human_bone_name, bone_name in manual_mapping.items():
        if bone_name not in armature_data.bones:
            skipped.append(
                {
                    "humanBone": human_bone_name,
                    "boneName": bone_name,
                    "reason": "missingBone",
                }
            )
            continue

        human_bone = getattr(human_bones, human_bone_name, None)
        if human_bone is None:
            skipped.append(
                {
                    "humanBone": human_bone_name,
                    "boneName": bone_name,
                    "reason": "missingHumanBoneSlot",
                }
            )
            continue

        human_bone.node.bone_name = bone_name
        applied.append({"humanBone": human_bone_name, "boneName": bone_name})
        mapped_human_bones[human_bone_name] = bone_name

    Vrm1HumanBonesPropertyGroup.update_all_bone_name_candidates(
        bpy.context, armature_data.name, force=True
    )
    error_messages = []
    if hasattr(human_bones, "error_messages"):
        error_messages = list(human_bones.error_messages())

    ancestry_validation = validate_humanoid_ancestry(armature, mapped_human_bones)
    error_messages.extend(ancestry_validation["errors"])

    return {
        "specVersion": ext.spec_version,
        "applied": applied,
        "skipped": skipped,
        "allRequiredBonesAssigned": human_bones.all_required_bones_are_assigned(),
        "allowNonHumanoidRig": human_bones.allow_non_humanoid_rig,
        "errorMessages": error_messages,
        "mappedHumanBones": {
            bone_property_to_export_name(human_bone): bone_name
            for human_bone, bone_name in mapped_human_bones.items()
        },
        "ancestryValidation": ancestry_validation,
        "unityHumanoidValid": human_bones.all_required_bones_are_assigned()
        and ancestry_validation["passed"],
    }


def patch_vrm_meta(path: Path, metadata: dict[str, object]) -> dict:
    version, chunks, json_index, json_obj = load_glb_structure(path)
    vrm_extension = json_obj.setdefault("extensions", {}).setdefault("VRMC_vrm", {})
    meta = vrm_extension.setdefault("meta", {})
    meta.update(metadata)
    write_glb_structure(path, version, chunks, json_index, json_obj)

    patched_meta = vrm_extension.get("meta", {})
    return {
        "name": patched_meta.get("name"),
        "authors": list(patched_meta.get("authors", [])),
        "version": patched_meta.get("version"),
        "references": list(patched_meta.get("references", [])),
    }


def load_glb_json(path: Path) -> dict:
    _version, _chunks, _json_index, json_obj = load_glb_structure(path)
    return json_obj


def inspect_source_glb_material_metadata() -> dict[str, dict[str, object]]:
    source_glb_path = find_source_glb_path()
    if source_glb_path is None:
        return {}

    glb_json = load_glb_json(source_glb_path)
    material_metadata = {}
    for material in glb_json.get("materials", []):
        name = material.get("name")
        if not isinstance(name, str) or not name:
            continue

        material_metadata[name] = {
            "alphaMode": material.get("alphaMode", "OPAQUE"),
            "doubleSided": bool(material.get("doubleSided", False)),
            "unlit": "KHR_materials_unlit" in material.get("extensions", {}),
        }

    return material_metadata


def patch_exported_material_settings(path: Path, material_metadata: dict[str, dict[str, object]]) -> dict[str, dict[str, object]]:
    version, chunks, json_index, glb_json = load_glb_structure(path)
    materials = glb_json.get("materials", [])
    patched = {}

    for material in materials:
        name = material.get("name")
        if not isinstance(name, str) or not name:
            continue

        source_info = material_metadata.get(name, {})
        alpha_mode = FACE_ALPHA_MODE_OVERRIDES.get(name, source_info.get("alphaMode"))
        if alpha_mode:
            material["alphaMode"] = alpha_mode
            if alpha_mode == "MASK":
                material["alphaCutoff"] = FACE_ALPHA_CUTOFF
            else:
                material.pop("alphaCutoff", None)

        if "doubleSided" in source_info:
            material["doubleSided"] = bool(source_info["doubleSided"])

        if alpha_mode or "doubleSided" in source_info:
            patched[name] = {
                "alphaMode": material.get("alphaMode", "OPAQUE"),
                "alphaCutoff": material.get("alphaCutoff"),
                "doubleSided": material.get("doubleSided"),
            }

    write_glb_structure(path, version, chunks, json_index, glb_json)
    return patched


def normalize_vrm_scene_root(path: Path) -> dict:
    version, chunks, json_index, glb_json = load_glb_structure(path)
    scene_index = int(glb_json.get("scene", 0))
    scene = glb_json["scenes"][scene_index]
    if not scene.get("nodes"):
        raise RuntimeError(f"VRM scene in '{path}' does not define any root nodes.")

    root_index = int(scene["nodes"][0])
    nodes = glb_json["nodes"]
    root_node = nodes[root_index]
    root_matrix = node_transform_to_matrix(root_node)
    child_indices = [child for child in root_node.get("children", []) if isinstance(child, int)]

    for child_index in child_indices:
        child_node = nodes[child_index]
        child_matrix = node_transform_to_matrix(child_node)
        set_node_transform_from_matrix(child_node, root_matrix @ child_matrix)

    root_node.pop("matrix", None)
    root_node.pop("translation", None)
    root_node.pop("rotation", None)
    root_node.pop("scale", None)

    write_glb_structure(path, version, chunks, json_index, glb_json)
    return inspect_vrm_scene_root(path)


def inspect_vrm_scene_root(path: Path) -> dict:
    glb_json = load_glb_json(path)
    scene_index = int(glb_json.get("scene", 0))
    scene = glb_json["scenes"][scene_index]
    if not scene.get("nodes"):
        raise RuntimeError(f"VRM scene in '{path}' does not define any root nodes.")

    root_index = int(scene["nodes"][0])
    root_node = glb_json["nodes"][root_index]
    translation = root_node.get("translation", [0.0, 0.0, 0.0])
    rotation = root_node.get("rotation", [0.0, 0.0, 0.0, 1.0])
    scale = root_node.get("scale", [1.0, 1.0, 1.0])

    return {
        "index": root_index,
        "name": root_node.get("name"),
        "translation": rounded_list(translation),
        "rotation": rounded_list(rotation),
        "scale": rounded_list(scale),
        "isIdentity": vector_is_close(translation, (0.0, 0.0, 0.0), GLTF_TRANSFORM_EPSILON)
        and quaternion_is_identity_xyzw(rotation, GLTF_TRANSFORM_EPSILON)
        and vector_is_close(scale, (1.0, 1.0, 1.0), GLTF_TRANSFORM_EPSILON),
    }


def inspect_vrm_humanoid_mapping(path: Path) -> dict[str, str]:
    glb_json = load_glb_json(path)
    nodes = glb_json.get("nodes", [])
    humanoid = (
        glb_json.get("extensions", {})
        .get("VRMC_vrm", {})
        .get("humanoid", {})
        .get("humanBones", {})
    )

    mapping = {}
    for human_bone_name, entry in humanoid.items():
        node_index = entry.get("node")
        if isinstance(node_index, int) and 0 <= node_index < len(nodes):
            mapping[human_bone_name] = nodes[node_index].get("name")
    return mapping


def export_vrm(path: Path, armature: bpy.types.Object, unity_profile: dict) -> tuple[dict, dict]:
    support = ensure_vrm_support()
    source_material_metadata = inspect_source_glb_material_metadata()
    if not support["operatorAvailable"]:
        raise RuntimeError(
            "VRM export operator is unavailable. Sync the official VRM addon into "
            ".blender-user-scripts/addons/io_scene_vrm and rerun export-vrm."
        )

    from io_scene_vrm.common.workspace import save_workspace
    from io_scene_vrm.editor import migration
    from io_scene_vrm.exporter.export_scene import collect_export_objects
    from io_scene_vrm.exporter.vrm1_exporter import Vrm1Exporter

    class PipelineExportPreferences:
        export_invisibles = False
        export_only_selections = True
        enable_advanced_preferences = False
        export_all_influences = False
        export_lights = False
        export_gltf_animations = False
        export_try_sparse_sk = False

    humanoid_details = configure_vrm_humanoid(armature)
    if not humanoid_details["allRequiredBonesAssigned"] or not humanoid_details["unityHumanoidValid"]:
        raise RuntimeError(
            "Required VRM humanoid bones are still unresolved after scripted mapping: "
            + "; ".join(humanoid_details["errorMessages"])
        )

    select_export_objects(armature)
    export_preferences = PipelineExportPreferences()
    armature_objects, export_objects = collect_export_objects(
        bpy.context,
        armature.name,
        export_preferences,
    )
    if armature not in armature_objects:
        raise RuntimeError(f"Armature '{armature.name}' was not collected for VRM export.")

    consume_tempdir_cleanup_warnings()
    export_temp_root = ensure_dir(path.parent / ".vrm-export-temp")
    original_tempdir = install_lenient_tempdir_cleanup(export_temp_root)
    try:
        with save_workspace(bpy.context, armature):
            migration.migrate(bpy.context, armature.name)
            exporter = Vrm1Exporter(bpy.context, export_objects, armature, export_preferences)
            glb_bytes = exporter.export()
    finally:
        restore_tempdir_cleanup(original_tempdir)
    if glb_bytes is None:
        raise RuntimeError("VRM exporter returned no data.")

    path.write_bytes(glb_bytes)
    normalize_vrm_scene_root(path)
    patched_meta = patch_vrm_meta(path, default_vrm_meta())
    patched_materials = patch_exported_material_settings(path, source_material_metadata)
    scene_root = inspect_vrm_scene_root(path)
    if not scene_root["isIdentity"]:
        raise RuntimeError(f"Exported VRM root node is not normalized: {scene_root}")

    exported_humanoid_mapping = inspect_vrm_humanoid_mapping(path)
    expected_humanoid_mapping = {
        bone_property_to_export_name(human_bone): bone_name
        for human_bone, bone_name in UNITY_HUMANOID_BONE_MAPPING.items()
    }
    mismatched_humanoid_mapping = {
        human_bone: {
            "expected": expected_bone,
            "actual": exported_humanoid_mapping.get(human_bone),
        }
        for human_bone, expected_bone in expected_humanoid_mapping.items()
        if exported_humanoid_mapping.get(human_bone) != expected_bone
    }
    if mismatched_humanoid_mapping:
        raise RuntimeError(
            "Exported VRM humanoid mapping does not match the Unity export profile: "
            + str(mismatched_humanoid_mapping)
        )

    final_size = path.stat().st_size
    unity_profile["ancestryValidation"] = humanoid_details["ancestryValidation"]
    unity_profile["exportedSceneRoot"] = scene_root
    unity_profile["exportedHumanBones"] = exported_humanoid_mapping

    export_details = {
        "humanoid": humanoid_details,
        "exporter": "Vrm1Exporter",
        "exportObjectCount": len(export_objects),
        "armatureObjectCount": len(armature_objects),
        "bytesWritten": final_size,
        "tempCleanupWarnings": consume_tempdir_cleanup_warnings(),
        "meta": patched_meta,
        "patchedMaterials": patched_materials,
        "unityProfile": unity_profile,
    }
    return support, export_details


def copy_source_glb(path: Path) -> Path | None:
    blend_dir = Path(bpy.data.filepath).resolve().parent
    candidates = sorted(blend_dir.glob("*.glb"))
    if not candidates:
        return None

    source = candidates[0]
    shutil.copy2(source, path)
    return path


def write_legacy_export_json(
    output_dir: Path,
    inspect_path: Path,
    preview_path: Path | None,
    fbx_path: Path | None,
    vrm_path: Path | None,
    glb_path: Path | None,
) -> None:
    result = {
        "inspect": str(inspect_path),
        "preview": path_or_none(preview_path),
        "fbx": path_or_none(fbx_path),
        "vrm": path_or_none(vrm_path),
        "glb": path_or_none(glb_path),
    }
    write_json(output_dir / "export.json", result)


def run_inspect(output_dir: Path, armature: bpy.types.Object, manifest_name: str) -> int:
    relinked_images = relink_missing_images()
    summary = scene_summary(armature, relinked_images)
    inspect_path = output_dir / "inspect.json"
    manifest_path = output_dir / manifest_name
    write_json(inspect_path, summary)
    manifest = build_manifest(
        command="inspect",
        summary=summary,
        inspect_path=inspect_path,
        source_blend="",
        status="completed",
        manifest_path=manifest_path,
        validation={"meshSkinningPassed": validate_mesh_skinning(summary)["passed"]},
    )
    write_json(manifest_path, manifest)
    print(f"Wrote scene summary to {inspect_path}")
    return 0


def run_export_fbx(
    output_dir: Path,
    armature: bpy.types.Object,
    source_blend: str,
    fbx_name: str,
    glb_name: str,
    preview_name: str,
    manifest_name: str,
) -> int:
    relinked_images = relink_missing_images()
    summary = scene_summary(armature, relinked_images)
    inspect_path = output_dir / "inspect.json"
    preview_path = output_dir / preview_name
    fbx_path = output_dir / fbx_name
    glb_path = output_dir / glb_name
    manifest_path = output_dir / manifest_name

    write_json(inspect_path, summary)
    render_preview(preview_path, armature_descendant_meshes(armature))
    export_fbx(fbx_path, armature)
    copied_glb = copy_source_glb(glb_path)

    manifest = build_manifest(
        command="export-fbx",
        summary=summary,
        inspect_path=inspect_path,
        source_blend=source_blend,
        status="completed",
        manifest_path=manifest_path,
        preview_path=preview_path,
        fbx_path=fbx_path,
        glb_path=copied_glb,
        validation={"meshSkinningPassed": validate_mesh_skinning(summary)["passed"]},
    )
    write_json(manifest_path, manifest)
    write_legacy_export_json(output_dir, inspect_path, preview_path, fbx_path, None, copied_glb)
    print(f"Wrote export outputs to {output_dir}")
    return 0


def run_export_vrm(
    output_dir: Path,
    armature: bpy.types.Object,
    source_blend: str,
    vrm_name: str,
    glb_name: str,
    preview_name: str,
    manifest_name: str,
) -> int:
    relinked_images = relink_missing_images()
    inspect_path = output_dir / "inspect.json"
    preview_path = output_dir / preview_name
    vrm_path = output_dir / vrm_name
    glb_path = output_dir / glb_name
    manifest_path = output_dir / manifest_name
    copied_glb = copy_source_glb(glb_path)

    try:
        unity_profile = prepare_unity_export_profile(armature)
        summary = scene_summary(armature, relinked_images)
        mesh_skinning = validate_mesh_skinning(summary)
        if not mesh_skinning["passed"]:
            skinning_errors = ", ".join(
                f"{issue['mesh']}:{issue['reason']}" for issue in mesh_skinning["issues"]
            )
            raise RuntimeError("Mesh skinning validation failed before export: " + skinning_errors)
        write_json(inspect_path, summary)
        converted_materials = normalize_materials_for_anime_vrm(inspect_source_glb_material_metadata())
        render_preview(preview_path, armature_descendant_meshes(armature))
        vrm_support, export_details = export_vrm(vrm_path, armature, unity_profile)
        export_details["materialMode"] = "anime-principled"
        export_details["materialCount"] = len(converted_materials)
        validation = build_export_validation(summary, export_details)
        enforce_export_validation(validation, export_details)
        manifest = build_manifest(
            command="export-vrm",
            summary=summary,
            inspect_path=inspect_path,
            source_blend=source_blend,
            status="completed",
            manifest_path=manifest_path,
            preview_path=preview_path,
            vrm_path=vrm_path,
            glb_path=copied_glb,
            vrm_support=vrm_support,
            export_details=export_details,
            validation=validation,
        )
        write_json(manifest_path, manifest)
        write_legacy_export_json(output_dir, inspect_path, preview_path, None, vrm_path, copied_glb)
        print(f"Wrote VRM export outputs to {output_dir}")
        return 0
    except Exception as exc:
        summary = scene_summary(armature, relinked_images)
        failed_validation = {"meshSkinningPassed": validate_mesh_skinning(summary)["passed"]}
        write_json(inspect_path, summary)
        render_preview(preview_path, armature_descendant_meshes(armature))
        manifest = build_manifest(
            command="export-vrm",
            summary=summary,
            inspect_path=inspect_path,
            source_blend=source_blend,
            status="failed",
            manifest_path=manifest_path,
            preview_path=preview_path,
            glb_path=copied_glb,
            vrm_support=detect_vrm_support(),
            validation=failed_validation,
            error=str(exc),
        )
        write_json(manifest_path, manifest)
        write_legacy_export_json(output_dir, inspect_path, preview_path, None, None, copied_glb)
        print(f"VRM export failed: {exc}")
        return 1


def normalize_command(command: str) -> str:
    if command == "export":
        return "export-fbx"
    return command


def main() -> int:
    global EXPORT_EXCLUDED_OBJECT_NAMES
    args = parse_args()
    EXPORT_EXCLUDED_OBJECT_NAMES = set(DEFAULT_EXPORT_EXCLUDED_OBJECT_NAMES)
    EXPORT_EXCLUDED_OBJECT_NAMES.update(args.exclude_object)
    output_dir = ensure_dir(Path(args.output_dir))
    armature = choose_armature(args.armature_name)
    command = normalize_command(args.command)

    if command == "inspect":
        return run_inspect(output_dir, armature, args.manifest_name)

    if command == "export-fbx":
        return run_export_fbx(
            output_dir,
            armature,
            args.source_blend,
            args.fbx_name,
            args.glb_name,
            args.preview_name,
            args.manifest_name,
        )

    if command == "export-vrm":
        return run_export_vrm(
            output_dir,
            armature,
            args.source_blend,
            args.vrm_name,
            args.glb_name,
            args.preview_name,
            args.manifest_name,
        )

    raise RuntimeError(f"Unsupported command '{args.command}'.")


if __name__ == "__main__":
    raise SystemExit(main())
