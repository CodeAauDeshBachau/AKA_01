# AR Sound Navigation

A prototype AR system designed to help users perceive spatial surroundings through sound cues. This project was developed for a hackathon and focuses on accessibility by providing audio-based navigation feedback in augmented reality environments.


## Overview

The AR Sound Navigation system leverages AR Foundation/ARCore to detect obstacles and spatial features in real time and converts this information into directional 3D audio cues. The goal is to give users a sense of nearby objects, distance, and direction using sound rather than visuals.

> ⚠️ **Note:** This is a hackathon prototype. The system is experimental and not intended for real-world navigation or as a substitute for assistive devices.

## Features

- Real-time environment scanning using AR Foundation.  
- Converts obstacle distance and direction into 3D audio cues.  
- Simple visualization for debugging purposes.  
- Basic filtering to ignore irrelevant surfaces like floors or ceilings.

## Technical Details

- **Platform:** Unity  
- **AR Framework:** AR Foundation / ARCore  
- **Audio Engine:** Resonance Audio (for spatial audio rendering)  
- **Detection:** Uses depth data and raycasts to identify nearby objects

## How It Works

1. The AR device scans the environment using its camera and depth sensors.  
2. Nearest obstacles are identified using depth information and raycasts.  
3. Directional audio cues are generated corresponding to the position of obstacles relative to the user.  
4. The system applies basic smoothing to avoid jittery audio feedback.

## Limitations

- Only works in areas visible to the camera; obstacles outside the view are not detected.  
- Accuracy depends on the device’s depth sensing capabilities.  
- Audio cues are approximate and may not reflect exact distances or positions.  
- Hackathon prototype — not tested for daily use or safety-critical navigation.

## Installation

1. Clone the repository:  
   ```bash
   git clone https://github.com/CodeAauDeshBachau/aka.git
