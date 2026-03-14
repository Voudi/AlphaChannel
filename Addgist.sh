#!/bin/bash
GIST="77c661ec1597925c2856d649a0cfc2d8"

git clone https://gist.github.com/Voudi/$GIST.git

cp AlphaChannel.zip $GIST/
cp pluginmaster.json $GIST/pluginmaster.json
cp AlphaChannel/images/icon.png $GIST/icon.png
cd $GIST
NEW_COMMIT=$(git commit-tree HEAD^{tree} -m "Reset Gist to latest state")
#git reset --hard $NEW_COMMIT
git show-ref 
git add AlphaChannel.zip
git add pluginmaster.json
git add icon.png

git commit -m "Adding Zip to Gist"
 
git push --force origin main
#If prompted, provide your Github credentials associated with the gist
cd ..
rm -rf $GIST
rm -rf AlphaChannel.zip
rm -rf pluginmaster.json