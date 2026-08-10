#!/usr/bin/env python3
"""
Export HD-BET (nnU-Net v2 backbone) to ONNX + plans metadata for Unity Sentis.

Usage:
  python export_hd_bet_onnx.py
  python export_hd_bet_onnx.py --output-dir ../../Assets/Models/HdBet --opset 17

Requires: pip install hd-bet torch onnx
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path


def _repo_assets_hd_bet_dir() -> Path:
    return Path(__file__).resolve().parents[2] / "Assets" / "Models" / "HdBet"


def export_hd_bet_onnx(output_dir: Path, opset: int, fold: str) -> None:
    # Quiet nnU-Net chatter during import, matching HD_BET.hd_bet_prediction.
    stdout = sys.stdout
    sys.stdout = open(os.devnull, "w")
    try:
        from HD_BET.checkpoint_download import maybe_download_parameters
        from HD_BET.paths import folder_with_parameter_files
        from nnunetv2.inference.predict_from_raw_data import nnUNetPredictor
    finally:
        sys.stdout.close()
        sys.stdout = stdout

    import torch

    os.environ["nnUNet_compile"] = "F"
    maybe_download_parameters()

    predictor = nnUNetPredictor(
        tile_step_size=0.5,
        use_gaussian=True,
        use_mirroring=False,
        perform_everything_on_device=False,
        device=torch.device("cpu"),
        verbose=False,
        verbose_preprocessing=False,
    )
    predictor.initialize_from_trained_model_folder(folder_with_parameter_files, fold)

    network = predictor.network.eval()
    configuration_manager = predictor.configuration_manager
    plans_manager = predictor.plans_manager

    patch_size = [int(v) for v in configuration_manager.patch_size]
    spacing = [float(v) for v in configuration_manager.spacing]

    channel_names = predictor.dataset_json.get("channel_names") or predictor.dataset_json.get("modality")
    if isinstance(channel_names, dict):
        num_input_channels = len(channel_names)
    elif hasattr(configuration_manager, "num_input_channels") and configuration_manager.num_input_channels:
        num_input_channels = int(configuration_manager.num_input_channels)
    else:
        num_input_channels = 1

    label_dict = predictor.dataset_json.get("labels") or {}
    if not isinstance(label_dict, dict):
        label_dict = {}
    num_output_channels = max(1, len(label_dict))

    class _PrimaryHeadWrapper(torch.nn.Module):
        """nnU-Net may return deep-supervision tuple; export only the full-res head."""

        def __init__(self, module: torch.nn.Module):
            super().__init__()
            self.module = module

        def forward(self, x: torch.Tensor) -> torch.Tensor:
            y = self.module(x)
            if isinstance(y, (list, tuple)):
                y = y[0]
            return y

    export_network = _PrimaryHeadWrapper(network).eval()
    dummy = torch.zeros(1, num_input_channels, *patch_size, dtype=torch.float32)
    with torch.no_grad():
        probe = export_network(dummy)
        if hasattr(probe, "shape") and len(probe.shape) >= 2:
            num_output_channels = int(probe.shape[1])

    output_dir.mkdir(parents=True, exist_ok=True)
    onnx_path = output_dir / "hd_bet.onnx"
    plans_path = output_dir / "hd_bet_plans.json"

    # Prefer legacy exporter when available for broader Sentis opset compatibility.
    export_kwargs = dict(
        export_params=True,
        opset_version=opset,
        do_constant_folding=True,
        input_names=["input"],
        output_names=["output"],
    )
    try:
        torch.onnx.export(
            export_network,
            dummy,
            str(onnx_path),
            dynamo=False,
            **export_kwargs,
        )
    except TypeError:
        torch.onnx.export(
            export_network,
            dummy,
            str(onnx_path),
            **export_kwargs,
        )

    # Collect useful preprocessing metadata for Unity reimplementation.
    transpose_forward = list(getattr(plans_manager, "transpose_forward", []) or [])
    transpose_backward = list(getattr(plans_manager, "transpose_backward", []) or [])
    normalization = None
    try:
        normalization = configuration_manager.normalization_schemes
    except Exception:
        normalization = None

    metadata = {
        "model_name": "HD-BET",
        "source": "https://github.com/MIC-DKFZ/HD-BET",
        "parameter_folder": str(folder_with_parameter_files),
        "fold": fold,
        "opset": opset,
        "onnx_file": onnx_path.name,
        "input_name": "input",
        "output_name": "output",
        "num_input_channels": num_input_channels,
        "num_output_channels": num_output_channels,
        "patch_size": patch_size,
        "spacing": spacing,
        "tile_step_size": 0.5,
        "use_gaussian": True,
        "use_mirroring_default": False,
        "transpose_forward": transpose_forward,
        "transpose_backward": transpose_backward,
        "normalization_schemes": normalization,
        "dataset_json": {
            "channel_names": channel_names,
            "labels": label_dict,
            "file_ending": predictor.dataset_json.get("file_ending"),
        },
        "notes": [
            "ONNX contains the nnU-Net backbone only.",
            "Unity must reimplement crop-to-nonzero, resample to spacing, z-score, sliding-window, and restore.",
            "HD-BET weights are typically CC BY-NC 4.0; verify license before commercial redistribution.",
        ],
    }

    plans_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(f"Wrote {onnx_path} ({onnx_path.stat().st_size / (1024 * 1024):.2f} MB)")
    print(f"Wrote {plans_path}")
    print(f"patch_size={patch_size}, spacing={spacing}, in={num_input_channels}, out={num_output_channels}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Export HD-BET nnU-Net backbone to ONNX for Unity Sentis.")
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=_repo_assets_hd_bet_dir(),
        help="Directory for hd_bet.onnx and hd_bet_plans.json",
    )
    parser.add_argument("--opset", type=int, default=17, help="ONNX opset version (Sentis prefers <=17).")
    parser.add_argument(
        "--fold",
        type=str,
        default="all",
        help="Fold identifier used by initialize_from_trained_model_folder (default: all).",
    )
    args = parser.parse_args()
    export_hd_bet_onnx(args.output_dir.resolve(), args.opset, args.fold)


if __name__ == "__main__":
    main()
