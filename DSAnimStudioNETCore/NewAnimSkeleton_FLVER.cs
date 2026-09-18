using DSAnimStudio.DebugPrimitives;
using Microsoft.Xna.Framework;
using SoulsFormats;
using SoulsAssetPipeline.Animation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static DSAnimStudio.NewAnimSkeleton_HKX;

namespace DSAnimStudio
{
    public class NewAnimSkeleton_FLVER : NewAnimSkeleton
    {
        protected override bool GetGlobalEnableDrawTransforms() => Main.HelperDraw.EnableFlverBoneTransforms;
        protected override bool GetGlobalEnableDrawLines() => Main.HelperDraw.EnableFlverBoneLines;
        protected override bool GetGlobalEnableDrawBoxes() => Main.HelperDraw.EnableFlverBoneBoxes;
        protected override bool GetGlobalEnableDrawText() => Main.HelperDraw.EnableFlverBoneNames;
        protected override Color GetDrawColorBoneBoxes() => Main.Colors.ColorHelperFlverBoneBoundingBox;
        protected override Color GetDrawColorBoneLines() => Main.Colors.ColorHelperFlverBoneLines;
        protected override Color GetDrawColorBoneTransforms() => Main.Colors.ColorHelperFlverBoneTransforms;
        protected override Color GetDrawColorBoneText() => Main.Colors.ColorHelperFlverBoneTexts;

        
        

        public Matrix[] ShaderMatricesNew = new Matrix[GFXShaders.FlverShader.BoneMatrixSize];

        public Matrix[] ShaderMatricesNew_RefPose = new Matrix[0];

        public struct SubmeshBoneMatrixMapEntry
        {
            public int SubmeshIndex;
            public int MatrixIndex;
        }

        public class SubmeshBoneMatrixMapStruct
        {
            public List<SubmeshBoneMatrixMapEntry> Entries = new List<SubmeshBoneMatrixMapEntry>();
        }
        
        private SubmeshBoneMatrixMapStruct[] submeshBoneMatrixMap;
        

        public void RegistPerMeshBoneIndices(int submeshIndex, int[] boneIndices)
        {
            for (int i = 0; i < boneIndices.Length; i++)
            {
                var map = boneIndices[i] >= 0 ? submeshBoneMatrixMap[boneIndices[i]] : new();
                var existing = map.Entries.FindIndex(x => x.SubmeshIndex == submeshIndex);
                if (existing < 0)
                {
                    map.Entries.Add(new SubmeshBoneMatrixMapEntry() { SubmeshIndex = submeshIndex, MatrixIndex = i });
                }
            }
        }

        void InitShaderMatrices()
        {
            ShaderMatricesNew = new Matrix[Bones.Count];
            ShaderMatricesNew_RefPose = new Matrix[Bones.Count];
            submeshBoneMatrixMap = new SubmeshBoneMatrixMapStruct[Bones.Count];

            for (int i = 0; i < Bones.Count; i++)
            {
                ShaderMatricesNew[i] = Matrix.Identity;
                ShaderMatricesNew_RefPose[i] = Matrix.Identity;
                submeshBoneMatrixMap[i] = new SubmeshBoneMatrixMapStruct();
            }
        }
        
        static NewAnimSkeleton_FLVER()
        {
            
        }

        

        public bool EnableRefPoseMatrices = true;

        

        public Model MODEL;
        public void LoadFLVERSkeleton(Model mdl, List<FLVER.Node> flverBones)
        {
            MODEL = mdl;

            int[] childCounts = new int[flverBones.Count];

            Bones.Clear();
            BoneIndices_ByName.Clear();
            TopLevelBoneIndices.Clear();

            for (int i = 0; i < flverBones.Count; i++)
            {
                if (flverBones[i].ParentIndex < 0)
                    TopLevelBoneIndices.Add(i);
                var newBone = new NewBone(flverBones[i], flverBones);
                newBone.Index = i;
                if (flverBones[i].ParentIndex >= 0)
                    childCounts[flverBones[i].ParentIndex]++;

                if (newBone.Name != null)
                    BoneIndices_ByName[newBone.Name] = i;

                Bones.Add(newBone);
            }
            
            InitBoneTree();
            
            InitShaderMatrices();
        }
        
        public void SetDebugWeightViewBoneIndex(int i)
        {
            if (MODEL.USE_GLOBAL_BONE_MATRIX || i < 0)
            {
                MODEL.SetDebugBoneWeightViewOfAllSubmeshes(i);
            }
            else
            {
                if (i >= 0)
                    MODEL.SetDebugBoneWeightViewOfAllSubmeshes(-2); //-2 makes it still operate in bone weight view mode but with no weights highlighted.
                else
                    MODEL.SetDebugBoneWeightViewOfAllSubmeshes(-1);

                foreach (var mapEntry in submeshBoneMatrixMap[i].Entries)
                {
                    MODEL.SetDebugBoneWeightViewOfSubmesh(mapEntry.SubmeshIndex, mapEntry.MatrixIndex);
                }
            }
            DebugViewWeightOfBone_ImguiIndex = i;
        }

        public static Matrix GetWeightedBoneDelta(NewBone bone, Matrix inverseReference)
        {
            return GetWeightedBoneDelta(bone,inverseReference,bone.FKMatrix);
        }

        public static Matrix GetWeightedBoneDelta(NewBone bone,Matrix inverseReference,Matrix fk)
        {
            var inputMatrix = inverseReference * fk;
            if (bone.Weight != 1)
            {
                inputMatrix = NewBlendableTransform.Lerp(NewBlendableTransform.Identity, inputMatrix.ToNewBlendableTransform(), bone.Weight).GetXnaMatrixFull();
            }
            return inputMatrix;
        }

        Matrix[] clothFrames,clothPending;
        bool[] clothAffected,clothNext;
        byte[] clothVisit;
        public int ClothDeformedBoneCount {get;private set;}
        Matrix[] rigidFrames,rigidPending;
        bool[] rigidAffected,rigidNext;
        byte[] rigidVisit;
        public int RigidDeformedBoneCount {get;private set;}
        public Matrix GetPhysicsInputBoneFK(int i)=>rigidAffected!=null&&i<rigidAffected.Length&&rigidAffected[i]?rigidFrames[i]:Bones[i].FKMatrix;
        public Matrix GetPreviewBoneFK(int i)=>clothAffected!=null&&i<clothAffected.Length&&clothAffected[i]?clothFrames[i]:GetPhysicsInputBoneFK(i);

        // Rigid output is a layer below Cloth. Neither layer writes animation FK.
        public void SetRigidBoneTransforms(IReadOnlyDictionary<int,Matrix> outputs)
        {
            if(outputs==null||outputs.Count==0){ClearRigidBoneTransforms();return;}
            if(rigidFrames==null||rigidFrames.Length!=Bones.Count)
            {
                rigidFrames=new Matrix[Bones.Count];rigidPending=new Matrix[Bones.Count];
                rigidAffected=new bool[Bones.Count];rigidNext=new bool[Bones.Count];rigidVisit=new byte[Bones.Count];
            }
            Array.Clear(rigidVisit);Array.Clear(rigidNext);
            Matrix Visit(int i)
            {
                if(rigidVisit[i]==2)return rigidPending[i];
                if(rigidVisit[i]==1)throw new InvalidOperationException("Cyclic rigid output skeleton");
                rigidVisit[i]=1;var bone=Bones[i];var value=bone.FKMatrix;
                if(outputs.TryGetValue(i,out var output)){value=output;rigidNext[i]=true;}
                else if(bone.ParentIndex>=0)
                {
                    int p=bone.ParentIndex;if(p>=Bones.Count)throw new InvalidOperationException("Invalid rigid output parent");
                    var parent=Visit(p);
                    if(rigidNext[p])
                    {
                        if(Bones[p].FKMatrix.Determinant()==0)throw new InvalidOperationException("Singular rigid output parent");
                        value=bone.FKMatrix*Matrix.Invert(Bones[p].FKMatrix)*parent;rigidNext[i]=true;
                    }
                }
                // Animated weapons may be intentionally hidden with tiny scale.
                // Rendering this output does not require inverting the matrix.
                if(rigidNext[i]&&!ClothPreviewData.Affine(value))throw new InvalidOperationException("Invalid rigid bone output transform: "+bone.Name);
                rigidPending[i]=value;rigidVisit[i]=2;return value;
            }
            foreach(int i in outputs.Keys)if((uint)i>=Bones.Count)throw new InvalidOperationException("Rigid output bone outside model");
            for(int i=0;i<Bones.Count;i++)Visit(i);
            (rigidFrames,rigidPending)=(rigidPending,rigidFrames);
            (rigidAffected,rigidNext)=(rigidNext,rigidAffected);RigidDeformedBoneCount=0;
            for(int i=0;i<Bones.Count;i++)
            {if(rigidAffected[i])RigidDeformedBoneCount++;if(rigidAffected[i]||rigidNext[i])CopyBoneToShaderMatrices(i);}
        }
        public void ClearRigidBoneTransforms()
        {
            if(rigidAffected==null)return;
            for(int i=0;i<rigidAffected.Length;i++)if(rigidAffected[i])
            {rigidAffected[i]=false;if(i<Bones.Count)CopyBoneToShaderMatrices(i);}
            RigidDeformedBoneCount=0;
        }

        // Separate output matrices keep simulated motion out of next frame's
        // animation/cloth inputs. Children inherit their animated local transform.
        public void SetClothBoneTransforms(IReadOnlyDictionary<int,Matrix> outputs)
        {
            if(outputs==null||outputs.Count==0){ClearClothBoneTransforms();return;}
            if(clothFrames==null||clothFrames.Length!=Bones.Count)
            {
                clothFrames=new Matrix[Bones.Count];clothPending=new Matrix[Bones.Count];
                clothAffected=new bool[Bones.Count];clothNext=new bool[Bones.Count];clothVisit=new byte[Bones.Count];
            }
            Array.Clear(clothVisit);Array.Clear(clothNext);
            Matrix Visit(int i)
            {
                if(clothVisit[i]==2)return clothPending[i];
                if(clothVisit[i]==1)throw new InvalidOperationException("Cyclic cloth output skeleton");
                clothVisit[i]=1;var bone=Bones[i];var value=GetPhysicsInputBoneFK(i);
                if(outputs.TryGetValue(i,out var output)){value=output;clothNext[i]=true;}
                else if(bone.ParentIndex>=0)
                {
                    int p=bone.ParentIndex;if(p>=Bones.Count)throw new InvalidOperationException("Invalid cloth output parent");
                    var parent=Visit(p);
                    if(clothNext[p])
                    {
                        var inputParent=GetPhysicsInputBoneFK(p);
                        if(Math.Abs(inputParent.Determinant())<1e-12f)throw new InvalidOperationException("Singular cloth output parent");
                        value=GetPhysicsInputBoneFK(i)*Matrix.Invert(inputParent)*parent;clothNext[i]=true;
                    }
                }
                if(clothNext[i]&&(!ClothPreviewData.Affine(value)||Math.Abs(value.Determinant())<1e-12f))throw new InvalidOperationException("Invalid cloth bone output transform");
                clothPending[i]=value;clothVisit[i]=2;return value;
            }
            foreach(int i in outputs.Keys)if((uint)i>=Bones.Count)throw new InvalidOperationException("Cloth output bone outside model");
            for(int i=0;i<Bones.Count;i++)Visit(i);
            (clothFrames,clothPending)=(clothPending,clothFrames);
            (clothAffected,clothNext)=(clothNext,clothAffected);
            ClothDeformedBoneCount=0;
            for(int i=0;i<Bones.Count;i++)
            {
                if(clothAffected[i])ClothDeformedBoneCount++;
                if(clothAffected[i]||clothNext[i])CopyBoneToShaderMatrices(i);
            }
        }

        public void ClearClothBoneTransforms()
        {
            if(clothAffected==null)return;
            for(int i=0;i<clothAffected.Length;i++)if(clothAffected[i])
            {clothAffected[i]=false;if(i<Bones.Count)CopyBoneToShaderMatrices(i);}
            ClothDeformedBoneCount=0;
        }

        public void CopyBoneToShaderMatrices(int i)
        {
            var bone = Bones[i];
            var fk=GetPreviewBoneFK(i);
            var inputMatrix = GetWeightedBoneDelta(bone, Matrix.Invert(bone.ReferenceFKMatrix),fk);


            if (i < ShaderMatricesNew.Length)
            {
                if (MODEL.USE_GLOBAL_BONE_MATRIX)
                {
                    ShaderMatricesNew[i] = inputMatrix;
                    if (EnableRefPoseMatrices)
                        ShaderMatricesNew_RefPose[i] = Bones[i].ReferenceFKMatrix * inputMatrix;
                }
                else
                {
                    if (EnableRefPoseMatrices)
                    {
                        var refPoseMatrix = Bones[i].ReferenceFKMatrix * inputMatrix;
                        foreach (var mapEntry in submeshBoneMatrixMap[i].Entries)
                        {
                            MODEL.SetBoneMatrixOfSubmesh(mapEntry.SubmeshIndex, mapEntry.MatrixIndex, inputMatrix);
                            MODEL.SetBoneMatrixOfSubmesh_RefPose(mapEntry.SubmeshIndex, mapEntry.MatrixIndex, refPoseMatrix);
                        }
                    }
                    else
                    {
                        foreach (var mapEntry in submeshBoneMatrixMap[i].Entries)
                        {
                            MODEL.SetBoneMatrixOfSubmesh(mapEntry.SubmeshIndex, mapEntry.MatrixIndex, inputMatrix);
                        }
                    }
                }
             

                
            }

            MODEL.DummyPolyMan?.UpdateFlverBone(i, inputMatrix, fk);
        }
        
        

        
        
    }
}
