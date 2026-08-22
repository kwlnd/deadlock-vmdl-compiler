import bpy
import math
from mathutils import Matrix, Euler

bl_info = {
    "name": "Deadlock Hero Model Fixer",
    "author": "Deadlock Modding Tools",
    "version": (1, 1, 0),
    "blender": (4, 0, 0),
    "location": "3D View > Sidebar (N) > Deadlock / Object Context Menu (RMB)",
    "description": "1-Click fix for Deadlock hero models imported from S2V GLTF: purges laggy animations, normalizes bone length to 1, removes $cloth bones, scales x40 for Source 2 units, rotates 90 deg along X, and transfers Vertex/Float Maps.",
    "category": "3D View",
}

class DEADLOCK_OT_fix_hero(bpy.types.Operator):
    """Purge laggy animations, fix armature bone lengths, remove $cloth bones, scale (x40) & rotate Deadlock model"""
    bl_idname = "object.deadlock_fix_hero"
    bl_label = "Fix Deadlock Hero Model"
    bl_options = {'REGISTER', 'UNDO'}

    purge_animations: bpy.props.BoolProperty(
        name="Purge Animations (Stop Lag)",
        description="Clear all animation data, actions, and NLA tracks that freeze Blender",
        default=True
    )

    delete_unneeded_objects: bpy.props.BoolProperty(
        name="Remove Icosphere & Dummy Objects",
        description="Delete Icosphere and glTF_not_exported objects",
        default=True
    )

    normalize_bone_length: bpy.props.BoolProperty(
        name="Set Bone Length to 1.0",
        description="Set length of all edit bones to 1.0",
        default=True
    )

    remove_cloth_bones: bpy.props.BoolProperty(
        name="Remove $cloth Bones",
        description="Remove cloth physics bones ($cloth*) that break custom rigging",
        default=True
    )

    scale_mode: bpy.props.EnumProperty(
        name="Scale Adjustment",
        description="Scale adjustment for model",
        items=[
            ('MULTIPLY_40', "Scale x40 (Source 2 Units)", "Multiply scale by 40 (1.0 -> 40.0)"),
            ('SET_ONE', "Reset Scale to 1.0", "Reset scale to (1, 1, 1) from 0.025"),
            ('NONE', "Don't change scale", "Keep current scale"),
        ],
        default='MULTIPLY_40'
    )

    rotate_x_90: bpy.props.BoolProperty(
        name="Rotate 90° (X-Axis)",
        description="Rotate model 90 degrees along X-axis to fix Source 2 coordinate orientation",
        default=True
    )

    apply_transforms: bpy.props.BoolProperty(
        name="Apply All Transforms",
        description="Apply location, rotation, and scale to armature and all meshes",
        default=True
    )

    def execute(self, context):
        # 1. Purge all animations across the entire file if requested
        purged_anims = 0
        if self.purge_animations:
            for obj in bpy.data.objects:
                if obj.animation_data:
                    obj.animation_data_clear()
                    purged_anims += 1
            for action in list(bpy.data.actions):
                bpy.data.actions.remove(action, do_unlink=True)
                purged_anims += 1

        # 2. Delete unwanted dummy objects
        deleted_objs = 0
        if self.delete_unneeded_objects:
            for obj in list(bpy.data.objects):
                name_low = obj.name.lower()
                if any(tag in name_low for tag in ["icosphere", "not_exported", "glTF_not_exported"]):
                    bpy.data.objects.remove(obj, do_unlink=True)
                    deleted_objs += 1

        # 3. Find Armatures
        armatures = [obj for obj in bpy.data.objects if obj.type == 'ARMATURE']
        if not armatures:
            self.report({'WARNING'}, "No armature found in the scene.")
            return {'FINISHED'}

        # Target the active or first armature
        target_arm = context.active_object if (context.active_object and context.active_object.type == 'ARMATURE') else armatures[0]
        context.view_layer.objects.active = target_arm

        # 4. Edit mode bone fixes
        cloth_removed = 0
        bones_fixed = 0

        if self.normalize_bone_length or self.remove_cloth_bones:
            bpy.ops.object.mode_set(mode='EDIT')
            for bone in list(target_arm.data.edit_bones):
                name_low = bone.name.lower()
                if self.remove_cloth_bones and ("$cloth" in name_low or "cloth_" in name_low or "_cloth" in name_low):
                    target_arm.data.edit_bones.remove(bone)
                    cloth_removed += 1
                else:
                    if self.normalize_bone_length:
                        bone.length = 1.0
                    bones_fixed += 1
            bpy.ops.object.mode_set(mode='OBJECT')

        # 5. Scale & Rotation fix
        if self.scale_mode == 'MULTIPLY_40':
            target_arm.scale = (target_arm.scale.x * 40.0, target_arm.scale.y * 40.0, target_arm.scale.z * 40.0)
            for child in target_arm.children:
                if child.type == 'MESH' and child.parent_type == 'OBJECT':
                    child.scale = (child.scale.x * 40.0, child.scale.y * 40.0, child.scale.z * 40.0)
        elif self.scale_mode == 'SET_ONE':
            target_arm.scale = (1.0, 1.0, 1.0)

        if self.rotate_x_90:
            target_arm.rotation_euler.x += math.radians(90)

        # 6. Apply Transforms
        if self.apply_transforms:
            # Apply to Armature
            context.view_layer.objects.active = target_arm
            target_arm.select_set(True)
            bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

            # Apply to all Meshes
            for obj in bpy.data.objects:
                if obj.type == 'MESH':
                    context.view_layer.objects.active = obj
                    obj.select_set(True)
                    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
                    obj.select_set(False)

        # Re-select armature
        context.view_layer.objects.active = target_arm
        target_arm.select_set(True)

        msg = f"Deadlock Model Fixed! Purged {purged_anims} anims, removed {cloth_removed} cloth bones, {bones_fixed} bones length=1.0, Scale={self.scale_mode}."
        self.report({'INFO'}, msg)
        return {'FINISHED'}

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, width=340)


class DEADLOCK_OT_transfer_maps(bpy.types.Operator):
    """Transfer Vertex Colors, Float Maps (cloth weights), and Vertex Groups from Reference Source Mesh to Active/Target Mesh"""
    bl_idname = "object.deadlock_transfer_maps"
    bl_label = "Transfer Source 2 Maps & Float Maps"
    bl_options = {'REGISTER', 'UNDO'}

    transfer_vertex_colors: bpy.props.BoolProperty(
        name="Transfer Vertex Colors (Paint/Blend)",
        description="Transfer valvesource_vertex_paint, valvesource_vertex_blend, etc.",
        default=True
    )

    transfer_float_maps: bpy.props.BoolProperty(
        name="Transfer Float Maps (Cloth)",
        description="Transfer all cloth_* vertex groups (cloth_enable, cloth_goal_strength, etc.) and remap ranges",
        default=True
    )

    transfer_vertex_groups: bpy.props.BoolProperty(
        name="Transfer Bone Vertex Groups",
        description="Transfer bone skinning/deform vertex groups",
        default=True
    )

    sample_method: bpy.props.EnumProperty(
        name="Mapping Method",
        description="Data transfer projection method",
        items=[
            ('INTERPOLATED', "Nearest Face Interpolated (Recommended)", "Smooth interpolation across nearest surface faces"),
            ('NEAREST', "Nearest Vertex", "Map to closest vertex"),
            ('PROJECTED', "Projected Face Interpolated", "Projected along surface normals"),
        ],
        default='INTERPOLATED'
    )

    def execute(self, context):
        selected_meshes = [obj for obj in context.selected_objects if obj.type == 'MESH']
        if len(selected_meshes) < 2:
            self.report({'ERROR'}, "Please select at least 2 mesh objects: Reference Mesh and Target Mesh (Target must be Active).")
            return {'CANCELLED'}

        target_obj = context.active_object
        if target_obj not in selected_meshes:
            target_obj = selected_meshes[0]

        source_obj = next((obj for obj in selected_meshes if obj != target_obj), None)
        if not source_obj:
            self.report({'ERROR'}, "Could not determine Source and Target meshes.")
            return {'CANCELLED'}

        # 1. Transfer Vertex Colors
        transferred_vc = 0
        if self.transfer_vertex_colors and source_obj.data.vertex_colors:
            dt = target_obj.modifiers.new(name="Temp_DT_VCOL", type='DATA_TRANSFER')
            dt.object = source_obj
            dt.use_loop_data = True
            dt.data_types_loops = {'VCOL'}
            dt.loop_mapping = 'NEAREST_POLYNOR' if self.sample_method == 'PROJECTED' else 'NEAREST_FACE'

            for vc in source_obj.data.vertex_colors:
                if vc.name not in target_obj.data.vertex_colors:
                    target_obj.data.vertex_colors.new(name=vc.name)
                transferred_vc += 1

            context.view_layer.objects.active = target_obj
            bpy.ops.object.datalayout_transfer(modifier=dt.name)
            bpy.ops.object.modifier_apply(modifier=dt.name)

        # 2. Transfer Vertex Groups & Float Maps
        transferred_vg = 0
        if (self.transfer_float_maps or self.transfer_vertex_groups) and source_obj.vertex_groups:
            dt_vg = target_obj.modifiers.new(name="Temp_DT_VG", type='DATA_TRANSFER')
            dt_vg.object = source_obj
            dt_vg.use_vert_data = True
            dt_vg.data_types_verts = {'VGROUP_WEIGHTS'}
            dt_vg.vert_mapping = 'NEAREST' if self.sample_method == 'NEAREST' else 'EDGEINTERP_NEAREST'

            for vg in source_obj.vertex_groups:
                is_cloth = "cloth_" in vg.name or vg.name.startswith("cloth")
                if is_cloth and not self.transfer_float_maps:
                    continue
                if not is_cloth and not self.transfer_vertex_groups:
                    continue

                if vg.name not in target_obj.vertex_groups:
                    target_obj.vertex_groups.new(name=vg.name)
                transferred_vg += 1

            context.view_layer.objects.active = target_obj
            bpy.ops.object.datalayout_transfer(modifier=dt_vg.name)
            bpy.ops.object.modifier_apply(modifier=dt_vg.name)

        # 3. Copy vertex_map_remaps if present
        remaps_copied = 0
        if hasattr(source_obj, "vs") and hasattr(target_obj, "vs") and hasattr(source_obj.vs, "vertex_map_remaps"):
            existing_target = {r.group for r in target_obj.vs.vertex_map_remaps}
            for src_remap in source_obj.vs.vertex_map_remaps:
                if src_remap.group not in existing_target:
                    dst = target_obj.vs.vertex_map_remaps.add()
                    dst.group = src_remap.group
                    dst.min = src_remap.min
                    dst.max = src_remap.max
                    remaps_copied += 1
                else:
                    for dst in target_obj.vs.vertex_map_remaps:
                        if dst.group == src_remap.group:
                            dst.min = src_remap.min
                            dst.max = src_remap.max

        self.report({'INFO'}, f"Transferred {transferred_vc} Color Maps, {transferred_vg} Vertex Groups/Float Maps from '{source_obj.name}' to '{target_obj.name}'.")
        return {'FINISHED'}

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, width=380)


class DEADLOCK_PT_sidebar_panel(bpy.types.Panel):
    """Deadlock Tools Sidebar Panel in 3D Viewport"""
    bl_label = "Deadlock Modding"
    bl_idname = "DEADLOCK_PT_sidebar_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'Deadlock'

    def draw(self, context):
        layout = self.layout
        col = layout.column(align=True)

        col.label(text="Hero Model Tools:", icon='ARMATURE_DATA')
        col.operator("object.deadlock_fix_hero", text="⚡ Fix Deadlock Hero Model", icon='MOD_ARMATURE')
        col.separator()

        col.label(text="Transfer Maps (Reference -> Custom):", icon='MOD_DATA_TRANSFER')
        col.operator("object.deadlock_transfer_maps", text="⚡ Transfer Maps & Float Maps", icon='IMPORT')
        col.separator()

        box = layout.box()
        box.label(text="Features:", icon='INFO')
        box.label(text="• Purges laggy animations & actions")
        box.label(text="• Sets all bone lengths to 1.0")
        box.label(text="• Removes $cloth physics bones")
        box.label(text="• Scales x40 for Source 2 units")
        box.label(text="• Rotates +90° along X-axis")
        box.label(text="• Transfers Float/Cloth Maps & Bone Weights")


def menu_func(self, context):
    self.layout.separator()
    self.layout.operator("object.deadlock_fix_hero", text="⚡ Fix Deadlock Hero Model", icon='ARMATURE_DATA')
    self.layout.operator("object.deadlock_transfer_maps", text="⚡ Transfer Source 2 Maps & Float Maps", icon='MOD_DATA_TRANSFER')


classes = (
    DEADLOCK_OT_fix_hero,
    DEADLOCK_OT_transfer_maps,
    DEADLOCK_PT_sidebar_panel,
)

def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.VIEW3D_MT_object_context_menu.append(menu_func)
    bpy.types.VIEW3D_MT_armature_context_menu.append(menu_func)

def unregister():
    bpy.types.VIEW3D_MT_object_context_menu.remove(menu_func)
    bpy.types.VIEW3D_MT_armature_context_menu.remove(menu_func)
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)

if __name__ == "__main__":
    register()
